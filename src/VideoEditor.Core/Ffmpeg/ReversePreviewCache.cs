using System.Globalization;
using VideoEditor.Core.Export;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>一個倒放片段需要的預覽檔：來源、入出點與預覽尺寸。</summary>
public sealed record ReversePreviewRequest(MediaInfo Media, double SourceIn, double SourceOut, Size2 Size);

/// <summary>
/// 倒放片段的預覽檔。LibVLC 不能倒著播放，所以先用 FFmpeg 產生一個已倒放、縮小的暫存檔，
/// 播放時改播這個檔案。檔案依來源與入出點快取，關閉時刪除；
/// 片段修剪、刪除或取消倒放後，以 <see cref="Sync"/> 停掉並刪除不再需要的預覽檔。
/// 產生方式與匯出相同（見 <see cref="ReverseGraph"/>）：每小段各跑一次 FFmpeg，記憶體用量不隨片段長度增加。
/// </summary>
/// <param name="progress">產生中回報 0–1；產生結束（成功或失敗）時回報 null。</param>
public sealed class ReversePreviewCache(FfmpegTools tools, string? directory = null, IProgress<double?>? progress = null) : IDisposable
{
    /// <summary>預設放預覽檔的上層資料夾；每次執行各用一個 reverse-xxxx 子資料夾。</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "VideoEditor");
    private const string FolderPrefix = "reverse-";
    /// <summary>使用中時一直獨佔開著的檔案；清理舊資料夾時，刪不掉它就表示還有程式在用。</summary>
    private const string LockFileName = ".in-use";

    private readonly string _directory = directory ?? Path.Combine(DefaultRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
    private FileStream? _lock;
    private sealed record Entry(Task<string> File, CancellationTokenSource Cancel);

    private readonly Dictionary<(string Path, long In, long Out, int Width, int Height), Entry> _files = [];
    // 一次只產生一個，避免同時跑多個 FFmpeg 拖慢播放。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();

    private static (string Path, long In, long Out, int Width, int Height) Key(MediaInfo media, double sourceIn, double sourceOut, Size2 size) =>
        (media.Path, Ticks(sourceIn), Ticks(sourceOut), size.Width, size.Height);

    /// <summary>取得（必要時開始產生）預覽檔。檔案從片段第一格（來源的 sourceOut 前一格）開始，長度等於片段。</summary>
    public Task<string> GetAsync(MediaInfo media, double sourceIn, double sourceOut, Size2 size)
    {
        var key = Key(media, sourceIn, sourceOut, size);
        lock (_files)
        {
            if (_files.TryGetValue(key, out var existing) && !existing.File.IsFaulted && !existing.File.IsCanceled) return existing.File;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
            var task = RenderAsync(media, sourceIn, sourceOut, size, cancel.Token);
            _files[key] = new Entry(task, cancel);
            return task;
        }
    }

    /// <summary>
    /// 讓快取只留下 wanted 列出的預覽檔：其他的若還在產生（或排隊）就中止，已產生的檔案刪除；
    /// wanted 裡還沒有的依序開始產生。時間軸每次編輯後呼叫，
    /// 修剪倒放片段後舊範圍的預覽檔才不會繼續占用 FFmpeg 與磁碟，新範圍也不必等到播放才開始產生。
    /// </summary>
    public void Sync(IReadOnlyCollection<ReversePreviewRequest> wanted)
    {
        var keep = wanted.Select(r => Key(r.Media, r.SourceIn, r.SourceOut, r.Size)).ToHashSet();
        List<Entry> dropped;
        lock (_files)
        {
            var keys = _files.Keys.Where(key => !keep.Contains(key)).ToList();
            dropped = keys.Select(key => _files[key]).ToList();
            foreach (var key in keys) _files.Remove(key);
        }
        foreach (var entry in dropped)
        {
            entry.Cancel.Cancel(); // 產生中的 FFmpeg 會立刻終止；產生到一半的檔案由 RenderAsync 自己刪
            entry.File.ContinueWith(task =>
            {
                // 已經做好的檔案：可能還被播放器開著一下子，刪不掉時留給關閉程式時整個資料夾一起刪。
                if (task.IsCompletedSuccessfully) TemporaryFiles.DeleteFile(task.Result);
                entry.Cancel.Dispose();
            }, TaskScheduler.Default);
        }
        foreach (var request in wanted) GetAsync(request.Media, request.SourceIn, request.SourceOut, request.Size);
    }

    /// <summary>把倒放好的中間檔編碼成預覽檔的參數。</summary>
    public static IReadOnlyList<string> EncodeArguments(MediaInfo media, string list, double duration, string output)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-v", "error", "-progress", "pipe:1", "-y", "-f", "concat", "-i", list, "-map", "0:v" };
        if (media.HasAudio) args.AddRange(["-map", "0:a", "-c:a", "aac", "-b:a", "160k"]);
        // 短 GOP：播放時常從片段中間開始，定位才快。
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "10", "-pix_fmt", "yuv420p",
            "-t", duration.ToString("0.######", CultureInfo.InvariantCulture), output]);
        return args;
    }

    private async Task<string> RenderAsync(MediaInfo media, double sourceIn, double sourceOut, Size2 size, CancellationToken token)
    {
        // 不回到呼叫端的執行緒：結束時刪暫存檔可能要等，別卡住畫面。進度由 IProgress 自己送回。
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var name = Guid.NewGuid().ToString("N");
        var output = Path.Combine(_directory, name + ".mp4");
        var work = Path.Combine(_directory, name + ".parts");
        var finished = false;
        try
        {
            Directory.CreateDirectory(work);
            // 標記這個資料夾正在使用，另一個同時執行的程式清理時才不會刪掉它。
            _lock ??= new FileStream(Path.Combine(_directory, LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var duration = sourceOut - sourceIn;
            // 先縮小再倒放，記憶體用量與中間檔大小都依預覽尺寸計算。
            var job = ReverseGraph.Plan("r", media, sourceIn, sourceOut, media.HasAudio,
                $"scale={size.Width}:{size.Height},setsar=1", ReverseGraph.FrameBytes(media, size.Width, size.Height));
            // 進度：倒放各小段與最後的編碼各算一半。
            var total = job.Duration + duration;
            progress?.Report(0);
            await ReverseGraph.RunAsync(tools, job, work, seconds => progress?.Report(Math.Clamp(seconds / total, 0, 1)), token).ConfigureAwait(false);
            var args = EncodeArguments(media, ReverseGraph.ResolvePath(job.ListPath, work), duration, output);
            await ProcessRunner.RunWithLinesAsync(tools.Ffmpeg, args, line =>
            {
                if (ExportService.ParseProgress(line, duration) is { } value) progress?.Report((job.Duration + value * duration) / total);
            }, token).ConfigureAwait(false);
            finished = true;
            return output;
        }
        finally
        {
            TemporaryFiles.DeleteDirectory(work);
            if (!finished) TemporaryFiles.DeleteFile(output);
            progress?.Report(null);
            _gate.Release();
        }
    }

    /// <summary>
    /// 刪掉以前留下的預覽資料夾（程式當掉或被強制結束時，Dispose 沒有執行）。
    /// 還有程式在用的資料夾（使用中標記刪不掉）會略過；刪不掉的檔案也略過，下次再試。回傳刪掉的資料夾數。
    /// </summary>
    public static int CleanupStale(string? root = null)
    {
        root ??= DefaultRoot;
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(root, FolderPrefix + "*"))
        {
            try
            {
                var marker = Path.Combine(folder, LockFileName);
                // 使用中的程式獨佔開著這個檔案，刪除會失敗，整個資料夾就保留。
                if (File.Exists(marker)) File.Delete(marker);
                Directory.Delete(folder, true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * 1_000_000);

    /// <summary>停止產生中的預覽檔並刪除暫存資料夾。檔案仍被佔用時會重試，最多約 2 秒。</summary>
    public void Dispose()
    {
        _disposed.Cancel(); // 會立刻終止產生中的 FFmpeg
        _lock?.Dispose();
        _lock = null;
        // 仍刪不掉就放棄：只是暫存目錄裡留下一些檔案，不影響程式關閉。
        TemporaryFiles.DeleteDirectory(_directory);
    }
}
