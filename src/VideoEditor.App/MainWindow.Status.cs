using System.Windows;
using System.Windows.Media;

namespace VideoEditor.App;

/// <summary>狀態列訊息的種類，決定圖示與顏色。</summary>
public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

public partial class MainWindow
{
    // ---- 狀態列 ----

    // 狀態列右邊按鈕的動作（例如匯出完成後的「開啟資料夾」）；下一則訊息出現時拿掉。
    private Action? _statusAction;

    /// <summary>
    /// 在視窗最下方的狀態列顯示訊息。空字串清除。訊息太長被截斷時，滑鼠移上去可以看完整內容。
    /// 給了 <paramref name="actionLabel"/> 與 <paramref name="action"/> 時，右邊多一個按鈕。
    /// </summary>
    private void ShowStatus(string message, StatusKind kind = StatusKind.Info, string? actionLabel = null, Action? action = null)
    {
        _statusAction = action;
        StatusAction.Content = actionLabel;
        StatusAction.Visibility = action != null && actionLabel != null ? Visibility.Visible : Visibility.Collapsed;
        var (icon, brush) = kind switch
        {
            StatusKind.Success => ("✔", Palette.StatusSuccess),
            StatusKind.Warning => ("⚠", Palette.StatusWarning),
            StatusKind.Error => ("✖", Palette.StatusError),
            _ => ("ⓘ", Palette.TextSecondary),
        };
        var empty = string.IsNullOrEmpty(message);
        StatusIcon.Text = empty ? "" : icon;
        StatusIcon.Foreground = brush;
        StatusText.Text = message;
        StatusText.Foreground = kind == StatusKind.Info ? Palette.TextSecondary : brush;
        StatusText.ToolTip = empty ? null : message;
    }

    private void OnStatusActionClick(object sender, RoutedEventArgs e) => _statusAction?.Invoke();

    /// <summary>錯誤訊息常有好幾行（例如 FFmpeg 的錯誤輸出），狀態列只放第一個非空白行。</summary>
    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
