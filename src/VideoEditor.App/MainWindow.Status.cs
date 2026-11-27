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

    /// <summary>
    /// 在視窗最下方的狀態列顯示訊息。空字串清除。訊息太長被截斷時，滑鼠移上去可以看完整內容。
    /// </summary>
    private void ShowStatus(string message, StatusKind kind = StatusKind.Info)
    {
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

    /// <summary>錯誤訊息常有好幾行（例如 FFmpeg 的錯誤輸出），狀態列只放第一個非空白行。</summary>
    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
