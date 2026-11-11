namespace VideoEditor.Core.Ffmpeg;

/// <summary>刪除 FFmpeg 寫過的暫存檔。</summary>
public static class TemporaryFiles
{
    // FFmpeg 剛被停止時可能還佔用檔案一下子；刪不掉就稍等再試。
    private const int DeleteAttempts = 20;
    private const int DeleteRetryDelayMs = 100;

    /// <summary>
    /// 刪除資料夾與裡面的檔案；不存在時不做事。檔案仍被佔用時會重試，最多約 2 秒。
    /// 仍刪不掉就放棄並回傳 false：只是留下一些暫存檔，不應該蓋掉原本的結果或錯誤。
    /// </summary>
    public static bool DeleteDirectory(string directory) => Retry(() =>
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    });

    /// <summary>刪除一個檔案，規則同 <see cref="DeleteDirectory"/>。</summary>
    public static bool DeleteFile(string path) => Retry(() => File.Delete(path));

    private static bool Retry(Action delete)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                delete();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts) return false;
                Thread.Sleep(DeleteRetryDelayMs);
            }
        }
    }
}
