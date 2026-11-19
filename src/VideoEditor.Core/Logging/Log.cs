namespace VideoEditor.Core.Logging;

/// <summary>
/// 全程式共用的紀錄入口。啟動時由 App 呼叫 <see cref="Initialize"/>；沒有初始化時（例如單元測試）什麼都不寫。
/// 紀錄只存在本機，內容可能含檔案路徑（常有使用者名稱），回報問題前請先檢查。
/// </summary>
public static class Log
{
    private static volatile FileLog? _sink;

    public static void Initialize(FileLog sink) => _sink = sink;

    /// <summary>紀錄資料夾；尚未初始化時為 null。</summary>
    public static string? Directory => _sink?.Directory;

    public static void Info(string message) => _sink?.Write("INFO", message);

    public static void Warn(string message, Exception? exception = null) => _sink?.Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => _sink?.Write("ERROR", message, exception);
}
