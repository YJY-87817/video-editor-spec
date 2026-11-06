using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using VideoEditor.App.Preview;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 進度面板 ----

    /// <summary>stage 為目前檔案內的進度（0–1）。全部完成時隱藏。</summary>
    private void ShowImportProgress(string name, string step, double stage)
    {
        if (_importTotal == 0)
        {
            ImportRow.Visibility = Visibility.Collapsed;
            UpdateProgressPanel();
            return;
        }
        var value = (_importDone + stage) / _importTotal;
        ImportText.Text = $"讀取素材 {_importDone + 1}/{_importTotal}：{name}（{step}）";
        ImportProgress.Value = value;
        ImportPercent.Text = $"{value * 100:0}%";
        ImportRow.Visibility = Visibility.Visible;
        UpdateProgressPanel();
    }

    private void ShowReverseProgress(double? value)
    {
        ReverseRow.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
        ReverseProgress.Value = value ?? 0;
        ReversePercent.Text = $"{(value ?? 0) * 100:0}%";
        UpdateProgressPanel();
    }

    private void UpdateProgressPanel() =>
        ProgressPanel.Visibility = ImportRow.Visibility == Visibility.Visible || ReverseRow.Visibility == Visibility.Visible
            || ExportRow.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小時 {span.Minutes} 分"
        : span.TotalMinutes >= 1 ? $"{span.Minutes} 分 {span.Seconds} 秒"
        : $"{Math.Max(1, span.Seconds)} 秒";
}
