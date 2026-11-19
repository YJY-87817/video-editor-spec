using System.ComponentModel;
using System.Text.Json;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>
/// 「預期會發生、要轉成使用者看得到的訊息」的例外分類，讓各處的 <c>catch … when</c> 共用同一份清單（Q-8）。
/// 只涵蓋外部世界造成的失敗：FFmpeg 行程與檔案系統。程式錯誤（ArgumentException、NullReferenceException…）不在其中，應該照常往外丟。
/// </summary>
public static class ExpectedErrors
{
    /// <summary>檔案系統的失敗：被其他程式佔用、磁碟錯誤、沒有權限。</summary>
    public static bool IsFile(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>FFmpeg／ffprobe 的失敗：執行結果不是 0，或行程根本無法啟動（被防毒軟體擋下、檔案被刪掉）。</summary>
    public static bool IsProcess(Exception e) => e is FfmpegException or Win32Exception;

    /// <summary>行程或檔案系統的失敗。</summary>
    public static bool IsFileOrProcess(Exception e) => IsFile(e) || IsProcess(e);

    /// <summary>讀取 ffprobe 輸出時的失敗：行程、檔案系統，或輸出的 JSON 不合格式。</summary>
    public static bool IsProbe(Exception e) => IsFileOrProcess(e) || e is InvalidDataException or JsonException;
}
