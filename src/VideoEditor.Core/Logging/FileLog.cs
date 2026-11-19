using System.Globalization;
using System.Text;

namespace VideoEditor.Core.Logging;

/// <summary>
/// 極簡的檔案紀錄：每天一個檔案（app-yyyyMMdd.log），超過大小就換新檔，只保留最近幾個。
/// 記錄本身絕不丟出例外，寫不進去（磁碟滿、權限不足、檔案被佔用）時直接放棄，不影響程式運作。
/// 為了避免同一個錯誤反覆發生時（例如逐格擷取影格一直失敗）把紀錄洗掉，
/// 每 <paramref name="windowMs"/> 毫秒最多寫 <paramref name="maxPerWindow"/> 筆，超過的略過，下一個區間開頭補一行統計。
/// 開頭的紀錄通常就是原始原因，所以留下的是前面的、略過的是後面的。
/// </summary>
/// <param name="ticks">單調遞增的毫秒時鐘，測試時可替換。</param>
public sealed class FileLog(string directory, long maxBytes = 1_000_000, int keep = 5,
    int maxPerWindow = 20, int windowMs = 10_000, Func<long>? ticks = null)
{
    private readonly object _gate = new();
    private readonly Func<long> _ticks = ticks ?? (() => Environment.TickCount64);
    private bool _directoryReady;
    private long _windowStart = long.MinValue;
    private int _written;
    private int _dropped;

    public string Directory { get; } = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "logs");

    public void Write(string level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            var now = _ticks();
            var skipped = 0;
            if (_windowStart == long.MinValue || now - _windowStart >= windowMs)
            {
                skipped = _dropped;
                _windowStart = now;
                _written = 0;
                _dropped = 0;
            }
            if (_written >= maxPerWindow)
            {
                _dropped++;
                return;
            }
            _written++;

            var text = new StringBuilder();
            if (skipped > 0) AppendEntry(text, "WARN", $"前一段時間紀錄過於頻繁，略過 {skipped} 筆");
            AppendEntry(text, level, message);
            if (exception != null) text.AppendLine(exception.ToString());
            Append(text.ToString());
        }
    }

    private static void AppendEntry(StringBuilder text, string level, string message) => text
        .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
        .Append(' ').Append(level.PadRight(5)).Append(' ').AppendLine(message);

    private void Append(string text)
    {
        try
        {
            // 資料夾只需建立一次；失敗後（例如被刪掉）下次寫入再試。
            if (!_directoryReady)
            {
                System.IO.Directory.CreateDirectory(Directory);
                _directoryReady = true;
            }
            var path = Path.Combine(Directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            var info = new FileInfo(path);
            var isNew = !info.Exists;
            if (!isNew && info.Length > maxBytes)
            {
                Roll(path);
                isNew = true;
            }
            File.AppendAllText(path, text, Encoding.UTF8);
            // 紀錄檔的數量只在出現新檔案時才會增加，平常寫入不必掃描資料夾。
            if (isNew) Prune(path);
        }
        // 記錄失敗不能反過來讓程式出錯（這裡通常正在處理另一個錯誤），所以所有例外都忽略。
        catch (Exception)
        {
            _directoryReady = false;
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
