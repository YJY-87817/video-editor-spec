using VideoEditor.Core.Logging;

namespace VideoEditor.Core.Ffmpeg;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// 依序尋找程式目錄下的 tools\ffmpeg、程式目錄、PATH。
/// PATH 裡不是完整路徑的項目（例如 <c>.</c>、<c>C:tools</c>、<c>\tools</c>）會被略過：它的意思取決於目前的工作目錄，可能指到使用者剛好開啟的資料夾，而不是安裝 FFmpeg 的位置。
/// </summary>
public static class FfmpegLocator
{
    public static FfmpegTools Locate(string? baseDirectory = null, string? pathVariable = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var directories = new List<string> { Path.Combine(baseDirectory, "tools", "ffmpeg"), baseDirectory };
        directories.AddRange((pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified));

        foreach (var directory in directories)
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe)) continue;
            Log.Info($"使用的 FFmpeg：{ffmpeg}");
            return new FfmpegTools(ffmpeg, ffprobe);
        }

        throw new FfmpegNotFoundException("找不到 ffmpeg.exe 與 ffprobe.exe。請放到程式目錄的 tools\\ffmpeg，或加入 PATH。");
    }
}
