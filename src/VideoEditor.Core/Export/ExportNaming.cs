namespace VideoEditor.Core.Export;

/// <summary>匯出對話方塊預設的檔名與資料夾（UIUX-PLAN #18）。</summary>
public static class ExportNaming
{
    // Windows 檔名不能用的字元。Path.GetInvalidFileNameChars 在 Linux 上只有 '/' 與 '\0'，所以自己列出來。
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private const int MaxBaseLength = 80;

    /// <summary>
    /// 「第一個片段的檔名_剪輯_yyyyMMdd-HHmm.mp4」。不能用的字元換成底線，原檔名太長時截短，
    /// 去掉結尾的空白與句點（Windows 不允許）。沒有片段時用「剪輯」。
    /// </summary>
    public static string DefaultFileName(string? firstClipPath, DateTime now)
    {
        var name = string.IsNullOrWhiteSpace(firstClipPath) ? "" : Path.GetFileNameWithoutExtension(firstClipPath.Replace('\\', '/').Split('/')[^1]);
        name = new string(name.Select(c => c < 32 || Invalid.Contains(c) ? '_' : c).ToArray());
        if (name.Length > MaxBaseLength) name = name[..MaxBaseLength];
        name = name.TrimEnd(' ', '.');
        var stamp = now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        return name.Length == 0 ? $"剪輯_{stamp}.mp4" : $"{name}_剪輯_{stamp}.mp4";
    }

    /// <summary>預設資料夾：上次匯出的資料夾還在就用它，否則用第一個片段所在的資料夾，都沒有就交給系統（null）。</summary>
    public static string? DefaultFolder(string? lastExportFolder, string? firstClipPath, Func<string, bool> exists)
    {
        if (!string.IsNullOrEmpty(lastExportFolder) && exists(lastExportFolder)) return lastExportFolder;
        var folder = string.IsNullOrEmpty(firstClipPath) ? null : Path.GetDirectoryName(firstClipPath);
        return !string.IsNullOrEmpty(folder) && exists(folder) ? folder : null;
    }
}
