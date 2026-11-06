using System.Windows;
using System.Windows.Controls;

namespace VideoEditor.App.Controls;

/// <summary>
/// 預覽畫面上的進度面板，同時可顯示讀取素材、準備倒放預覽、匯出三種工作。
/// 沒有工作時整個面板收起；各列的顯示與隱藏由呼叫端決定。
/// </summary>
public partial class ProgressOverlay : UserControl
{
    public ProgressOverlay()
    {
        InitializeComponent();
    }

    /// <summary>按下「取消匯出」。</summary>
    public event Action? CancelExportRequested;

    // ---- 讀取素材 ----

    public void ShowImport(string text, double value)
    {
        ImportText.Text = text;
        ImportProgress.Value = value;
        ImportPercent.Text = $"{value * 100:0}%";
        ImportRow.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    public void HideImport()
    {
        ImportRow.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    // ---- 倒放預覽 ----

    /// <summary>value 為 null 時收起這一列。</summary>
    public void ShowReverse(double? value)
    {
        ReverseRow.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
        ReverseProgress.Value = value ?? 0;
        ReversePercent.Text = $"{(value ?? 0) * 100:0}%";
        UpdateVisibility();
    }

    // ---- 匯出 ----

    public void StartExport(string title)
    {
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportPercent.Text = "0%";
        ExportText.Text = title;
        ExportRow.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    public void ReportExport(double value, string text)
    {
        ExportProgress.Value = value;
        ExportPercent.Text = $"{value * 100:0}%";
        ExportText.Text = text;
    }

    /// <summary>匯出成功：顯示 100% 與完成訊息，並收起取消按鈕。</summary>
    public void FinishExport(string text)
    {
        ExportProgress.Value = 1;
        ExportPercent.Text = "100%";
        ExportText.Text = text;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>匯出結束（無論成功、取消或失敗）：取消按鈕不能再按。</summary>
    public void EndExport() => CancelButton.IsEnabled = false;

    public void HideExport()
    {
        ExportRow.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelExportRequested?.Invoke();

    private void UpdateVisibility() =>
        Visibility = ImportRow.Visibility == Visibility.Visible || ReverseRow.Visibility == Visibility.Visible
            || ExportRow.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
}
