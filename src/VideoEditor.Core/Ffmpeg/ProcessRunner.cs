using System.Diagnostics;
using System.Text;
using VideoEditor.Core.Logging;

namespace VideoEditor.Core.Ffmpeg;

public class FfmpegException(string message, string stderr) : Exception(message)
{
    public string Stderr { get; } = stderr;
}

/// <summary>FFmpeg 或 ffprobe 超過時間限制沒有完成，已被終止。是 <see cref="FfmpegException"/>，既有的錯誤處理會一併涵蓋。</summary>
public sealed class FfmpegTimeoutException(string message) : FfmpegException(message, "");

/// <summary>短暫工作的時間上限。超過代表檔案損毀或磁碟卡住，與其讓畫面一直等，不如終止並說明。</summary>
public static class FfmpegTimeouts
{
    public static readonly TimeSpan Probe = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Thumbnail = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Frame = TimeSpan.FromSeconds(30);
    /// <summary>一次解出多格（倒帶用）。</summary>
    public static readonly TimeSpan FrameRange = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan EncoderList = TimeSpan.FromSeconds(30);
}

public static class ProcessRunner
{
    private const int MaxErrorLines = 40;

    /// <summary>診斷用：每啟動一個程序就通知（測試用它量測 FFmpeg 的記憶體用量）。處理常式不可丟出例外。</summary>
    public static event Action<Process>? Started;

    private static Process Start(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"無法啟動 {exe}");
        Started?.Invoke(process);
        return process;
    }

    private static async Task<string> ReadErrorTailAsync(Process process)
    {
        var tail = new Queue<string>();
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > MaxErrorLines) tail.Dequeue();
        }
        return string.Join('\n', tail);
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        // 程序已經結束或正在結束時，Kill 可能丟出這兩種例外；本來就是要它結束，忽略即可。
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task FinishAsync(Process process, string exe, Task<string> error, CancellationToken token)
    {
        var stderr = await error.ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            // 命令列與錯誤輸出的尾段是事後查問題最需要的資料。命令列可能含檔案路徑，所以只寫進本機紀錄。
            Log.Error($"{Path.GetFileName(exe)} 結束代碼 {process.ExitCode}\n命令列：{Quote(exe, process.StartInfo.ArgumentList)}\n錯誤輸出（最後 {MaxErrorLines} 行）：\n{stderr}");
            throw new FfmpegException($"{Path.GetFileName(exe)} 結束代碼 {process.ExitCode}", stderr);
        }
    }

    private static string Quote(string exe, IEnumerable<string> args) =>
        string.Join(' ', args.Prepend(exe).Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));

    /// <summary>
    /// 在 <paramref name="timeout"/> 內沒做完就取消（會終止子行程）並丟出 <see cref="FfmpegTimeoutException"/>。
    /// 呼叫端自己取消時仍是 <see cref="OperationCanceledException"/>。timeout 為 null 表示不限時（匯出、波形這類本來就很久的工作）。
    /// </summary>
    private static async Task<T> WithTimeoutAsync<T>(string exe, TimeSpan? timeout, CancellationToken token, Func<CancellationToken, Task<T>> run)
    {
        if (timeout is not { } limit) return await run(token).ConfigureAwait(false);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(limit);
        try
        {
            return await run(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timer.IsCancellationRequested)
        {
            Log.Error($"{Path.GetFileName(exe)} 超過 {limit.TotalSeconds:0} 秒沒有完成，已終止");
            throw new FfmpegTimeoutException($"{Path.GetFileName(exe)} 超過 {limit.TotalSeconds:0} 秒沒有完成，已停止。檔案可能損毀，或放在回應很慢的磁碟上。");
        }
    }

    /// <summary>執行並回傳標準輸出的全部位元組；兩個管線同時讀取以免死結。</summary>
    public static Task<byte[]> RunForBytesAsync(string exe, IEnumerable<string> args, CancellationToken token = default, TimeSpan? timeout = null) =>
        WithTimeoutAsync(exe, timeout, token, async token =>
        {
            using var process = Start(exe, args);
            using var kill = token.Register(() => KillQuietly(process));
            try
            {
                var error = ReadErrorTailAsync(process);
                using var output = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None).ConfigureAwait(false);
                await FinishAsync(process, exe, error, token).ConfigureAwait(false);
                return output.ToArray();
            }
            catch
            {
                // 任何例外都不能留下仍在跑的子行程（using 只會釋放 Process 物件，不會結束它）。
                KillQuietly(process);
                throw;
            }
        });

    /// <summary>
    /// 執行並把標準輸出切成固定大小的區塊（例如一格 rawvideo 影格），最多 maxChunks 塊，結尾不足一塊的部分捨棄。
    /// 每塊直接讀進自己的陣列，不經過整段緩衝再複製，解多格影格時記憶體配置少很多。
    /// </summary>
    public static Task<IReadOnlyList<byte[]>> RunForChunksAsync(string exe, IEnumerable<string> args, int chunkSize, int maxChunks,
        CancellationToken token = default, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return WithTimeoutAsync(exe, timeout, token, async token =>
        {
            using var process = Start(exe, args);
            using var kill = token.Register(() => KillQuietly(process));
            try
            {
                var error = ReadErrorTailAsync(process);
                var output = process.StandardOutput.BaseStream;
                var chunks = new List<byte[]>();
                var buffer = new byte[chunkSize];
                while (chunks.Count < maxChunks)
                {
                    if (await output.ReadAtLeastAsync(buffer, chunkSize, throwOnEndOfStream: false, CancellationToken.None).ConfigureAwait(false) < chunkSize) break;
                    chunks.Add(buffer);
                    // 讀滿上限時不再配置下一塊。
                    if (chunks.Count < maxChunks) buffer = new byte[chunkSize];
                }
                // 讀滿上限後仍要把剩下的輸出讀完，程序才不會因管線塞滿而卡住。
                await output.CopyToAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
                await FinishAsync(process, exe, error, token).ConfigureAwait(false);
                return (IReadOnlyList<byte[]>)chunks;
            }
            catch
            {
                KillQuietly(process);
                throw;
            }
        });
    }

    /// <summary>
    /// 執行並把標準輸出交給 consume 邊讀邊處理（例如解出整段聲音來算波形），不必整段放進記憶體。
    /// consume 沒讀完的部分會在之後讀掉，程序才不會因管線塞滿而卡住。
    /// </summary>
    public static async Task RunWithStreamAsync(string exe, IEnumerable<string> args, Func<Stream, Task> consume,
        CancellationToken token = default)
    {
        using var process = Start(exe, args);
        using var kill = token.Register(() => KillQuietly(process));
        try
        {
            var error = ReadErrorTailAsync(process);
            var output = process.StandardOutput.BaseStream;
            await consume(output).ConfigureAwait(false);
            await output.CopyToAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
            await FinishAsync(process, exe, error, token).ConfigureAwait(false);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
    }

    /// <summary>執行並逐行回報標準輸出，用於讀取 -progress 進度。onLine 可能在背景執行緒呼叫。</summary>
    public static async Task RunWithLinesAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken token = default)
    {
        using var process = Start(exe, args);
        using var kill = token.Register(() => KillQuietly(process));
        try
        {
            var error = ReadErrorTailAsync(process);
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line) onLine(line);
            await FinishAsync(process, exe, error, token).ConfigureAwait(false);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
    }
}
