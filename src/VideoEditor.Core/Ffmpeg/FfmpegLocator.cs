namespace VideoEditor.Core.Ffmpeg;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// 依序尋找使用者自己選的資料夾（設定檔）、程式目錄下的 tools\ffmpeg、程式目錄、PATH。
/// PATH 裡的相對路徑（例如 <c>.</c>、<c>tools</c>）會被略過：它們依目前的工作目錄解析，
/// 從哪個資料夾啟動程式（例如按兩下別人給的檔案、從下載資料夾開啟）就會找到哪裡的 FFmpeg，是 PATH 裡最不該信任的項目。
/// </summary>
public static class FfmpegLocator
{
    /// <param name="baseDirectory">程式所在資料夾；預設為程式目錄。</param>
    /// <param name="pathVariable">PATH 環境變數的內容；預設讀取目前的環境變數，測試時可注入。</param>
    /// <param name="preferredDirectory">
    /// 使用者在「找不到 FFmpeg」畫面或狀態列選的資料夾，最先找。必須是完整路徑（理由同 PATH 的相對路徑）；
    /// 資料夾不在或裡面找不到程式時，照原本的順序找。
    /// </param>
    public static FfmpegTools Locate(string? baseDirectory = null, string? pathVariable = null, string? preferredDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        pathVariable ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDirectory) && Path.IsPathFullyQualified(preferredDirectory)) directories.Add(preferredDirectory);
        directories.Add(Path.Combine(baseDirectory, "tools", "ffmpeg"));
        directories.Add(baseDirectory);
        directories.AddRange(PathDirectories(pathVariable));

        foreach (var directory in directories)
        {
            var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
            var ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
        }

        throw new FfmpegNotFoundException("找不到 ffmpeg.exe 與 ffprobe.exe。請放到程式目錄的 tools\\ffmpeg，或加入 PATH。");
    }

    /// <summary>這個資料夾裡有沒有 ffmpeg.exe 與 ffprobe.exe（使用者選資料夾時檢查）。</summary>
    public static bool ContainsTools(string directory) =>
        Path.IsPathFullyQualified(directory)
        && File.Exists(Path.Combine(directory, "ffmpeg.exe"))
        && File.Exists(Path.Combine(directory, "ffprobe.exe"));

    /// <summary>找不到時給人看的「找過哪些地方」，依搜尋順序。</summary>
    public static IReadOnlyList<string> SearchedPlaces(string? baseDirectory = null, string? preferredDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var places = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDirectory)) places.Add($"自己選的資料夾：{preferredDirectory}");
        places.Add(Path.Combine(baseDirectory, "tools", "ffmpeg"));
        places.Add(baseDirectory);
        places.Add("PATH 環境變數裡的資料夾");
        return places;
    }

    /// <summary>PATH 的各個項目：去掉 Windows 常見的外層引號，並略過不是完整路徑的項目。</summary>
    public static IEnumerable<string> PathDirectories(string pathVariable) =>
        pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(entry => entry.Length > 0 && Path.IsPathFullyQualified(entry));
}
