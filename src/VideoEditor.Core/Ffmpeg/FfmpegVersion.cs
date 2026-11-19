using System.ComponentModel;
using System.Text;
using VideoEditor.Core.Logging;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>讀出實際使用的 FFmpeg 版本，讓使用者與紀錄看得出程式用的是哪一份。</summary>
public static class FfmpegVersion
{
    /// <summary>
    /// 執行 <c>ffmpeg -version</c>，回傳第一行去掉版權說明的部分（例如「ffmpeg version 6.1.1-3ubuntu5」）。
    /// 讀不到時回傳 null：這只是顯示用的資訊，不該讓程式因此出錯。
    /// </summary>
    public static async Task<string?> ReadAsync(FfmpegTools tools)
    {
        try
        {
            var output = await ProcessRunner.RunForBytesAsync(tools.Ffmpeg, ["-version"],
                timeout: FfmpegTimeouts.Default.EncoderList, logFailure: false).ConfigureAwait(false);
            return FirstLine(Encoding.UTF8.GetString(output));
        }
        catch (Exception e) when (e is FfmpegException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀不到 FFmpeg 的版本：{tools.Ffmpeg}", e is FfmpegException ? null : e);
            return null;
        }
    }

    /// <summary>第一行，並去掉「Copyright」之後的版權說明；沒有內容時為 null。</summary>
    public static string? FirstLine(string output)
    {
        var line = output.Split('\n', 2)[0].Trim();
        var copyright = line.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
        if (copyright > 0) line = line[..copyright].TrimEnd();
        return line.Length == 0 ? null : line;
    }
}
