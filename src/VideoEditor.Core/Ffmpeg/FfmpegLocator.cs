namespace VideoEditor.Core.Ffmpeg;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>依序尋找程式目錄下的 tools\ffmpeg、程式目錄、PATH。</summary>
public static class FfmpegLocator
{
    public static FfmpegTools Locate(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var directories = new List<string> { Path.Combine(baseDirectory, "tools", "ffmpeg"), baseDirectory };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var directory in directories)
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
        }

        throw new FfmpegNotFoundException("找不到 ffmpeg.exe 與 ffprobe.exe。請放到程式目錄的 tools\\ffmpeg，或加入 PATH。");
    }
}
