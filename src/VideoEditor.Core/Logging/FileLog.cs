using System.Globalization;
using System.Text;

namespace VideoEditor.Core.Logging;

/// <summary>
/// 極簡的檔案紀錄：每天一個檔案（app-yyyyMMdd.log），超過大小就換新檔，只保留最近幾個。
/// 記錄本身絕不丟出例外，寫不進去（磁碟滿、權限不足、檔案被佔用）時直接放棄，不影響程式運作。
/// </summary>
public sealed class FileLog(string directory, long maxBytes = 1_000_000, int keep = 5)
{
    private readonly object _gate = new();

    public string Directory { get; } = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "logs");

    public void Write(string level, string message, Exception? exception = null)
    {
        var text = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.PadRight(5)).Append(' ').AppendLine(message);
        if (exception != null) text.AppendLine(exception.ToString());

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) Roll(path);
                File.AppendAllText(path, text.ToString(), Encoding.UTF8);
                Prune(path);
            }
            // 記錄失敗不能反過來讓程式出錯（這裡通常正在處理另一個錯誤），所以所有例外都忽略。
            catch (Exception)
            {
            }
        }
    }

    /// <summary>把已滿的檔案改名成帶時間的封存檔，之後的紀錄寫進新的當日檔案。</summary>
    private static void Roll(string path)
    {
        var archive = Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:HHmmssfff}.log");
        File.Move(path, archive, overwrite: true);
    }

    /// <summary>
    /// 只保留最近修改的 <c>keep</c> 個紀錄檔。剛寫入的 <paramref name="current"/> 一定保留：
    /// 時間戳相同（檔案系統精度不足）時，不能讓排序的巧合刪掉剛寫的紀錄。
    /// </summary>
    private void Prune(string current)
    {
        var old = new DirectoryInfo(Directory).GetFiles("app-*.log")
            .Where(file => !string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(Math.Max(keep - 1, 0));
        foreach (var file in old)
        {
            try { file.Delete(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
