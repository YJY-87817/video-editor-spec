using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>
/// 在背景產生素材的波形，以檔案路徑為鍵，同一素材只算一次（同一支影片切多段也共用）。
/// 一次只跑一個 FFmpeg，避免拖慢預覽播放。結果只放在記憶體，關閉程式時丟掉；Dispose 會停掉進行中的工作。
/// </summary>
public sealed class WaveformCache(Func<MediaInfo, CancellationToken, Task<WaveformData>> extract) : IDisposable
{
    private readonly Dictionary<string, Task<WaveformData?>> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WaveformData> _ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();

    public WaveformCache(WaveformExtractor extractor) : this(extractor.ExtractAsync) { }

    /// <summary>波形產生完成。在背景執行緒觸發。</summary>
    public event Action<MediaInfo>? Ready;
    /// <summary>波形產生失敗（不影響其他功能）。在背景執行緒觸發。</summary>
    public event Action<MediaInfo, Exception>? Failed;

    /// <summary>排入背景產生（沒有音訊的素材不處理）。回傳的工作完成時得到波形，失敗或取消時得到 null，不會丟出例外。</summary>
    public Task<WaveformData?> Request(MediaInfo media)
    {
        if (!media.HasAudio) return Task.FromResult<WaveformData?>(null);
        lock (_tasks)
        {
            if (_tasks.TryGetValue(media.Path, out var existing)) return existing;
            if (_disposed.IsCancellationRequested) return Task.FromResult<WaveformData?>(null);
            var task = Task.Run(() => RunAsync(media, _disposed.Token));
            _tasks[media.Path] = task;
            return task;
        }
    }

    /// <summary>已產生好的波形；還沒好、失敗或沒有音訊時為 null。</summary>
    public WaveformData? Get(MediaInfo media)
    {
        lock (_tasks) return _ready.GetValueOrDefault(media.Path);
    }

    /// <summary>已排入但還沒完成。</summary>
    public bool IsPending(MediaInfo media)
    {
        lock (_tasks) return _tasks.TryGetValue(media.Path, out var task) && !task.IsCompleted;
    }

    private async Task<WaveformData?> RunAsync(MediaInfo media, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token);
            WaveformData data;
            try
            {
                data = await extract(media, token);
            }
            finally
            {
                _gate.Release();
            }
            lock (_tasks) _ready[media.Path] = data;
            Ready?.Invoke(media);
            return data;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        // 背景工作沒有人等待結果：任何錯誤都只回報，不丟出去。
        catch (Exception e)
        {
            if (token.IsCancellationRequested) return null;
            Failed?.Invoke(media, e);
            return null;
        }
    }

    public void Dispose() => _disposed.Cancel();
}
