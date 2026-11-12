using System.Diagnostics;
using System.Text;

namespace VideoEditor.Core.Ffmpeg;

public sealed class FfmpegException(string message, string stderr) : Exception(message)
{
    public string Stderr { get; } = stderr;
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
        while (await process.StandardError.ReadLineAsync() is { } line)
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
        var stderr = await error;
        await process.WaitForExitAsync(CancellationToken.None);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new FfmpegException($"{Path.GetFileName(exe)} 結束代碼 {process.ExitCode}", stderr);
    }

    /// <summary>執行並回傳標準輸出的全部位元組；兩個管線同時讀取以免死結。</summary>
    public static async Task<byte[]> RunForBytesAsync(string exe, IEnumerable<string> args, CancellationToken token = default)
    {
        using var process = Start(exe, args);
        using var kill = token.Register(() => KillQuietly(process));
        var error = ReadErrorTailAsync(process);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
        await FinishAsync(process, exe, error, token);
        return output.ToArray();
    }

    /// <summary>
    /// 執行並把標準輸出切成固定大小的區塊（例如一格 rawvideo 影格），最多 maxChunks 塊，結尾不足一塊的部分捨棄。
    /// 每塊直接讀進自己的陣列，不經過整段緩衝再複製，解多格影格時記憶體配置少很多。
    /// </summary>
    public static async Task<IReadOnlyList<byte[]>> RunForChunksAsync(string exe, IEnumerable<string> args, int chunkSize, int maxChunks,
        CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        using var process = Start(exe, args);
        using var kill = token.Register(() => KillQuietly(process));
        var error = ReadErrorTailAsync(process);
        var output = process.StandardOutput.BaseStream;
        var chunks = new List<byte[]>();
        var buffer = new byte[chunkSize];
        while (chunks.Count < maxChunks)
        {
            if (await output.ReadAtLeastAsync(buffer, chunkSize, throwOnEndOfStream: false, CancellationToken.None) < chunkSize) break;
            chunks.Add(buffer);
            // 讀滿上限時不再配置下一塊。
            if (chunks.Count < maxChunks) buffer = new byte[chunkSize];
        }
        // 讀滿上限後仍要把剩下的輸出讀完，程序才不會因管線塞滿而卡住。
        await output.CopyToAsync(Stream.Null, CancellationToken.None);
        await FinishAsync(process, exe, error, token);
        return chunks;
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
        var error = ReadErrorTailAsync(process);
        var output = process.StandardOutput.BaseStream;
        try
        {
            await consume(output);
        }
        catch
        {
            KillQuietly(process);
            throw;
        }
        await output.CopyToAsync(Stream.Null, CancellationToken.None);
        await FinishAsync(process, exe, error, token);
    }

    /// <summary>執行並逐行回報標準輸出，用於讀取 -progress 進度。</summary>
    public static async Task RunWithLinesAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken token = default)
    {
        using var process = Start(exe, args);
        using var kill = token.Register(() => KillQuietly(process));
        var error = ReadErrorTailAsync(process);
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line) onLine(line);
        await FinishAsync(process, exe, error, token);
    }
}
