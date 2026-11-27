using System.IO;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.Core.Models;
using VideoEditor.Core.Settings;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 記住介面設定（%LOCALAPPDATA%\VideoEditor\settings.json） ----

    private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultPath);
    // 讀進來的設定。視窗、版面、勾選與比例在關閉時直接從畫面讀；這裡只另外記上次的資料夾。
    private AppSettings _settings = new();

    /// <summary>在建構式裡、視窗顯示之前套用，視窗才不會先出現在預設位置再跳過去。</summary>
    private void ApplySettings()
    {
        _settings = _settingsStore.Load();

        if (_settings.Window is { } bounds
            && bounds.IsReachableOn(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(MinWidth, bounds.Width);
            Height = Math.Max(MinHeight, bounds.Height);
        }
        // 先設好一般狀態的位置再最大化，取消最大化時才會回到這個位置。
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        SetLength(LeftColumn, _settings.LeftPanelWidth);
        SetLength(RightColumn, _settings.RightPanelWidth);
        if (_settings.TimelineHeight is { } height && double.IsFinite(height))
            TimelineRow.Height = new GridLength(Math.Max(TimelineRow.MinHeight, height));

        SilenceSnapCheck.IsChecked = TimelineView.SnapToSilence = _settings.SnapToSilence;
        ClipSnapCheck.IsChecked = TimelineView.SnapToClips = _settings.SnapToClips;

        RatioButton(_settings.Ratio).IsChecked = true;
        (_settings.Mode == FitMode.Pad ? ModePad : ModeFill).IsChecked = true;
    }

    private static void SetLength(ColumnDefinition column, double? width)
    {
        if (width is { } value && double.IsFinite(value)) column.Width = new GridLength(Math.Max(column.MinWidth, value));
    }

    private RadioButton RatioButton(AspectRatio ratio) =>
        ratio == AspectRatio.R16x9 ? Ratio16x9
        : ratio == AspectRatio.R9x16 ? Ratio9x16
        : ratio == AspectRatio.R1x1 ? Ratio1x1
        : ratio == AspectRatio.R4x5 ? Ratio4x5
        : RatioOriginal;

    /// <summary>關閉時從畫面讀出目前的狀態存起來。存不進去只記錄，不影響關閉。</summary>
    private void SaveSettings()
    {
        // 最大化或最小化時，Left／Width 是最大化後的值；RestoreBounds 才是一般狀態的位置。
        var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        _settingsStore.Save(_settings with
        {
            Window = normal.IsEmpty ? _settings.Window : new WindowBounds(normal.Left, normal.Top, normal.Width, normal.Height),
            WindowMaximized = WindowState == WindowState.Maximized,
            LeftPanelWidth = LeftColumn.ActualWidth,
            RightPanelWidth = RightColumn.ActualWidth,
            TimelineHeight = TimelineRow.ActualHeight,
            SnapToSilence = SilenceSnapCheck.IsChecked == true,
            SnapToClips = ClipSnapCheck.IsChecked == true,
            Ratio = SelectedRatio,
            Mode = SelectedMode,
        });
    }

    /// <summary>上次的資料夾還在才用，否則交給對話方塊自己決定。</summary>
    private static string? ExistingFolder(string? folder) =>
        !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;

    private void RememberImportFolder(string file) =>
        _settings = _settings with { LastImportFolder = Path.GetDirectoryName(file) };

    private void RememberExportFolder(string file) =>
        _settings = _settings with { LastExportFolder = Path.GetDirectoryName(file) };
}
