using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.Core.Settings;

/// <summary>
/// 關閉時記住、下次開啟時還原的介面設定。只放介面偏好，不放時間軸或素材（規格不含專案存檔）。
/// 每個欄位都要有預設值：舊版的設定檔缺少新欄位時就用預設值。
/// </summary>
public sealed record AppSettings
{
    /// <summary>視窗一般狀態（非最大化）的位置與大小；null 表示還沒存過，交給系統決定。</summary>
    public WindowBounds? Window { get; init; }
    public bool WindowMaximized { get; init; }

    /// <summary>素材清單（左欄）、右欄的寬度與時間軸的高度；null 用 XAML 的預設值。</summary>
    public double? LeftPanelWidth { get; init; }
    public double? RightPanelWidth { get; init; }
    public double? TimelineHeight { get; init; }

    public bool SnapToSilence { get; init; } = true;
    public bool SnapToClips { get; init; } = true;

    /// <summary>存成 {"W":16,"H":9}；原尺寸是 {"W":0,"H":0}。</summary>
    public AspectRatio Ratio { get; init; } = AspectRatio.Original;
    public FitMode Mode { get; init; } = FitMode.Fill;

    /// <summary>時間顯示成時間碼（時:分:秒:格）還是百分秒。</summary>
    public TimeDisplay TimeDisplay { get; init; } = TimeDisplay.Timecode;

    /// <summary>使用者自己選的 FFmpeg 資料夾，找 FFmpeg 時排第一；null 表示照預設順序找。</summary>
    public string? FfmpegFolder { get; init; }

    /// <summary>上次選檔匯入、上次匯出的資料夾。</summary>
    public string? LastImportFolder { get; init; }
    public string? LastExportFolder { get; init; }
}

/// <summary>視窗的位置與大小（裝置無關像素）。</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height)
{
    /// <summary>
    /// 視窗的標題列有一塊落在螢幕範圍內，才拖得回來。外接螢幕拔掉、解析度變小後，
    /// 存下的位置可能整個在畫面外，這時不要用。螢幕範圍是所有螢幕合起來的矩形（虛擬螢幕）。
    /// </summary>
    public bool IsReachableOn(double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        const double minVisibleWidth = 100;
        const double titleBarHeight = 30;
        if (Width <= 0 || Height <= 0 || !double.IsFinite(Left) || !double.IsFinite(Top)) return false;
        var visibleLeft = Math.Max(Left, screenLeft);
        var visibleRight = Math.Min(Left + Width, screenLeft + screenWidth);
        var titleVisible = Top >= screenTop && Top + titleBarHeight <= screenTop + screenHeight;
        return titleVisible && visibleRight - visibleLeft >= minVisibleWidth;
    }
}

/// <summary>
/// 讀寫設定檔（JSON）。讀不到、格式錯誤時用預設值並記一筆警告，不讓程式打不開；
/// 寫入時先寫暫存檔再取代，寫到一半當掉也不會留下壞掉的設定檔。兩者都不丟出例外。
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Warn($"讀取設定檔失敗，改用預設值：{Path}", e);
            return new AppSettings();
        }
    }

    /// <summary>
    /// 檔案可能被手動改過：畫面比例只接受程式提供的幾種（不是的話回到原尺寸），列舉值超出範圍時用預設值。
    /// </summary>
    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        Ratio = settings.Ratio is { } ratio && (ratio.IsOriginal || AspectRatio.Presets.Contains(ratio)) ? ratio : AspectRatio.Original,
        Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : FitMode.Fill,
        TimeDisplay = Enum.IsDefined(settings.TimeDisplay) ? settings.TimeDisplay : TimeDisplay.Timecode,
    };

    public bool Save(AppSettings settings)
    {
        var temp = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"儲存設定檔失敗：{Path}", e);
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}

/// <summary>還原分隔線位置時，讓存下的寬高配合目前的視窗大小。</summary>
public static class PanelLayout
{
    /// <summary>
    /// 左右兩欄的寬度。兩欄加上中間欄的最小寬度放得下就照存的值；放不下時兩欄按比例縮小，但不小於各自的最小寬度。
    /// 沒存過（null）的欄位回傳 null，交給 XAML 的預設值。
    /// </summary>
    public static (double? Left, double? Right) FitSides(double? left, double? right, double available, double leftMin, double rightMin, double centerMin)
    {
        if (left is not { } l || !double.IsFinite(l)) left = null;
        if (right is not { } r || !double.IsFinite(r)) right = null;
        var room = available - centerMin;
        var total = (left ?? leftMin) + (right ?? rightMin);
        if (total <= room || total <= 0) return (left, right);
        var scale = Math.Max(0, room) / total;
        return (left is { } a ? Math.Max(leftMin, a * scale) : null, right is { } b ? Math.Max(rightMin, b * scale) : null);
    }

    /// <summary>時間軸的高度：上方保住最小高度，放不下時縮小，但不小於時間軸的最小高度。</summary>
    public static double FitBottom(double height, double available, double bottomMin, double topMin) =>
        Math.Max(bottomMin, Math.Min(height, available - topMin));
}
