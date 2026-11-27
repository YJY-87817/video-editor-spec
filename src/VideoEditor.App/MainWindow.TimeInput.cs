using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 時間的顯示（時間碼或百分秒）與鍵盤輸入 ----

    private enum TimeTarget { Position, In, Out }

    private TimeDisplay _timeDisplay = TimeDisplay.Timecode;
    private TimeTarget _timeTarget;

    /// <summary>
    /// 目前畫面上時間對應的影格率：匯入影片預覽用該素材的；剪輯預覽用輸出基準（和匯出相同），時間軸是空的時用 30。
    /// 圖片沒有影格率，由 <see cref="Timecode.EffectiveFps"/> 換成 30。
    /// </summary>
    /// 播放時每個 tick 都會用到，所以算一次存起來；時間軸、預覽模式或選取的素材改變時清掉（<see cref="InvalidateFps"/>）。
    private double CurrentFps() => _fps ??= Timecode.EffectiveFps(IsTimelinePreview
        ? ExportPlanner.Reference(_timeline)?.Media.FrameRate.Value ?? 0
        : _selectedMedia?.Info.FrameRate.Value ?? 0);

    private double? _fps;

    private void InvalidateFps() => _fps = null;

    private string ShowTime(double seconds) => Timecode.Display(seconds, CurrentFps(), _timeDisplay);

    private void OnTimeFormatClick(object sender, RoutedEventArgs e)
    {
        _timeDisplay = _timeDisplay == TimeDisplay.Timecode ? TimeDisplay.Decimal : TimeDisplay.Timecode;
        RefreshTimeTexts();
    }

    /// <summary>切換顯示方式或切換預覽（影格率可能不同）後，重寫畫面上所有的時間。</summary>
    private void RefreshTimeTexts()
    {
        InvalidateFps();
        TimeFormatButton.ToolTip = _timeDisplay == TimeDisplay.Timecode
            ? "目前顯示 時:分:秒:格；按一下改成 時:分:秒.百分秒"
            : "目前顯示 時:分:秒.百分秒；按一下改成 時:分:秒:格";
        TimelineView.RulerDisplay = _timeDisplay;
        TimelineView.RulerFps = Timecode.EffectiveFps(ExportPlanner.Reference(_timeline)?.Media.FrameRate.Value ?? 0);
        TimelineView.InvalidateVisual();
        if (_engine != null) TimeText.Text = $"{ShowTime(_engine.Position)} / {ShowTime(_engine.Duration)}";
        UpdateMarks();
    }

    private void OnTimeTextClick(object sender, MouseButtonEventArgs e) => OpenTimeInput(TimeTarget.Position, TimeText);
    private void OnMarkInTextClick(object sender, MouseButtonEventArgs e) => OpenTimeInput(TimeTarget.In, MarkInText);
    private void OnMarkOutTextClick(object sender, MouseButtonEventArgs e) => OpenTimeInput(TimeTarget.Out, MarkOutText);

    /// <summary>Ctrl+G：跳到指定時間。正在輸入文字時歸文字框。回傳 true 表示已處理。</summary>
    private bool HandleGoToKey(KeyEventArgs e)
    {
        if (e.Key != Key.G || Keyboard.Modifiers != ModifierKeys.Control || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return false;
        OpenTimeInput(TimeTarget.Position, TimeText);
        e.Handled = true;
        return true;
    }

    /// <summary>在那段時間文字旁彈出輸入框，填入目前的值並全選，直接打字就會取代。</summary>
    private void OpenTimeInput(TimeTarget target, FrameworkElement anchor)
    {
        if (_engine == null) return;
        if (target != TimeTarget.Position && (_selectedMedia is not { Info.IsStill: false } || IsTimelinePreview)) return;
        _engine.Pause();
        _timeTarget = target;
        TimePopupTitle.Text = target switch
        {
            TimeTarget.In => "入點（Enter 套用，Esc 取消）",
            TimeTarget.Out => "出點（Enter 套用，Esc 取消）",
            _ => IsTimelinePreview ? "跳到時間軸的時間（Enter 跳過去）" : "跳到影片的時間（Enter 跳過去）",
        };
        TimeInputBox.Text = ShowTime(CurrentTimeOf(target));
        TimeInputBox.Tag = null;
        TimePopup.PlacementTarget = anchor;
        TimePopup.IsOpen = true;
        // 彈出視窗出現之後才能把焦點交給輸入框。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TimeInputBox.Focus();
            Keyboard.Focus(TimeInputBox);
            TimeInputBox.SelectAll();
        });
    }

    private double CurrentTimeOf(TimeTarget target) => target switch
    {
        TimeTarget.In => _points.InPoint,
        TimeTarget.Out => _points.OutPoint(_selectedMedia!.Info),
        _ => _engine!.Position,
    };

    private void OnTimeInputKeyDown(object sender, KeyEventArgs e)
    {
        // 開始修改就拿掉上次的紅框。
        if (e.Key is not (Key.Enter or Key.Return or Key.Escape)) TimeInputBox.Tag = null;
        if (e.Key == Key.Escape)
        {
            TimePopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            if (ApplyTimeInput()) TimePopup.IsOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>關掉時把焦點還給時間軸，快捷鍵才會立刻有作用。</summary>
    private void OnTimePopupClosed(object? sender, EventArgs e) => TimelineView.Focus();

    /// <summary>套用輸入的時間；看不懂或不合規則時外框變紅、狀態列說明原因，回傳 false（輸入框不關）。</summary>
    private bool ApplyTimeInput()
    {
        if (_engine == null) return true;
        var fps = CurrentFps();
        var parsed = Timecode.Parse(TimeInputBox.Text, fps, CurrentTimeOf(_timeTarget));
        if (parsed.Seconds is not { } requested) return Reject(parsed.Error ?? Timecode.Help);

        var end = _timeTarget == TimeTarget.Position ? _engine.Duration : InOutPoints.MediaEnd(_selectedMedia!.Info);
        var time = Math.Clamp(requested, 0, Math.Max(0, end));
        var clampNote = requested < 0 ? "（超出範圍，停在開頭）" : requested > end ? "（超出範圍，停在結尾）" : "";

        switch (_timeTarget)
        {
            case TimeTarget.Position when IsTimelinePreview:
                // 對齊到輸出的影格，和匯出看到的一樣。
                time = Math.Min(end, Timecode.AlignToFrame(time, fps));
                TimelineView.Playhead = time;
                _engine.Seek(time);
                break;
            case TimeTarget.Position:
                time = _selectedMedia == null ? time : InOutPoints.Snap(time, _selectedMedia.Info);
                _engine.Seek(time);
                break;
            case TimeTarget.In:
                time = InOutPoints.Snap(time, _selectedMedia!.Info);
                if (_points.Out is { } outPoint && time >= outPoint - 1e-9) return Reject("入點要在出點之前。");
                _points.SetIn(time);
                _engine.Seek(time);
                UpdateMarks();
                break;
            case TimeTarget.Out:
                time = InOutPoints.Snap(time, _selectedMedia!.Info);
                if (time <= _points.InPoint + 1e-9) return Reject("出點要在入點之後。");
                _points.SetOut(time);
                _engine.Seek(time);
                UpdateMarks();
                break;
        }
        var what = _timeTarget switch { TimeTarget.In => "入點設為", TimeTarget.Out => "出點設為", _ => "已跳到" };
        ShowStatus($"{what} {ShowTime(time)}{clampNote}", clampNote.Length > 0 ? StatusKind.Warning : StatusKind.Info);
        return true;
    }

    private bool Reject(string message)
    {
        // 紅框由 App.xaml 的輸入框樣式依 Tag 顯示，取得焦點時也看得到。
        TimeInputBox.Tag = "error";
        ShowStatus(message, StatusKind.Warning);
        TimeInputBox.SelectAll();
        return false;
    }
}
