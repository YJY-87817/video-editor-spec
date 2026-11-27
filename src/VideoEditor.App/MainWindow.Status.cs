using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VideoEditor.Core.Models;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 狀態列 ----

    // 狀態列右邊按鈕的動作（例如匯出完成後的「開啟資料夾」）；下一則訊息出現時拿掉。
    private Action? _statusAction;

    /// <summary>
    /// 在視窗最下方的狀態列顯示訊息。空字串清除。訊息太長被截斷時，滑鼠移上去可以看完整內容。
    /// 給了 <paramref name="actionLabel"/> 與 <paramref name="action"/> 時，右邊多一個按鈕。
    /// 一般與完成訊息幾秒後淡出（<see cref="StatusPolicy"/>）；<paramref name="keep"/> 用於還在進行中的工作。
    /// </summary>
    private void ShowStatus(string message, StatusKind kind = StatusKind.Info, string? actionLabel = null, Action? action = null, bool keep = false)
    {
        SetStatusOpacity(null);
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
        ScheduleStatusFade(empty ? null : StatusPolicy.Lifetime(kind, action != null, keep));
    }

    private DispatcherTimer? _statusTimer;

    /// <summary>時間到就淡出；滑鼠停在狀態列上時先不消失，移開後再等一輪。新訊息會取消舊的計時。</summary>
    private void ScheduleStatusFade(TimeSpan? lifetime)
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        if (lifetime is not { } wait) return;
        var timer = new DispatcherTimer { Interval = wait };
        timer.Tick += (_, _) =>
        {
            if (StatusBar.IsMouseOver) return; // 計時器照走，下一輪再看
            timer.Stop();
            if (_statusTimer != timer) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3));
            fade.Completed += (_, _) =>
            {
                // 淡出期間出現新訊息時，新訊息已經把透明度設回來，這裡不能清掉它。
                if (_statusTimer == timer) ShowStatus("");
            };
            SetStatusOpacity(fade);
        };
        _statusTimer = timer;
        timer.Start();
    }

    /// <summary>只淡出訊息本身（圖示、文字、按鈕），右邊的 FFmpeg 版本不動。null 表示停止動畫、回到不透明。</summary>
    private void SetStatusOpacity(DoubleAnimation? animation)
    {
        foreach (var element in new UIElement[] { StatusIcon, StatusText, StatusAction })
        {
            element.BeginAnimation(UIElement.OpacityProperty, animation);
            if (animation == null) element.Opacity = 1;
        }
    }

    // 預覽引擎上一次顯示的訊息（例如「正在準備倒放預覽…」）。
    private string? _engineStatus;

    /// <summary>
    /// 預覽引擎的狀態：有訊息就顯示；空字串表示它的訊息結束了，只在狀態列還是那則訊息時才清掉，
    /// 不會把之後別人顯示的訊息（例如匯出完成與「開啟資料夾」）一起清掉。
    /// </summary>
    private void OnEngineStatus(string message)
    {
        if (message.Length > 0)
        {
            _engineStatus = message;
            // 引擎的訊息（準備倒放預覽）表示工作還在進行，留到引擎說結束為止。
            ShowStatus(message, keep: true);
        }
        else if (_engineStatus != null && StatusText.Text == _engineStatus)
        {
            _engineStatus = null;
            ShowStatus("");
        }
    }

    private void OnStatusActionClick(object sender, RoutedEventArgs e) => _statusAction?.Invoke();

    /// <summary>錯誤訊息常有好幾行（例如 FFmpeg 的錯誤輸出），狀態列只放第一個非空白行。</summary>
    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
}
