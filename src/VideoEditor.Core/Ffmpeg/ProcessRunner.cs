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
        return Process.Start(info) ?? throw new InvalidOperationException($"無法啟動 {exe}");
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
