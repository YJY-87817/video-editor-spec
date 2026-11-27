using System.Windows;
using System.Windows.Input;
using VideoEditor.Core.Editing;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 復原與重做 ----

    // 時間軸上拖曳片段（移動、修剪）開始時的樣子；放開滑鼠時記成一步。
    private TimelineSnapshot? _timelineDragBefore;

    private void OnTimelineEditStarted()
    {
        _engine?.Pause();
        _timelineDragBefore = _history.Capture();
    }

    /// <summary>時間軸上的拖曳結束。點了片段沒拖動時時間軸沒變，Commit 不會留下紀錄。</summary>
    private void OnTimelineEditCompleted(string label)
    {
        if (_timelineDragBefore is { } before) _history.Commit(before, label);
        _timelineDragBefore = null;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => StepHistory(redo: false);
    private void OnRedoClick(object sender, RoutedEventArgs e) => StepHistory(redo: true);

    /// <summary>
    /// Ctrl+Z 復原；Ctrl+Y、Ctrl+Shift+Z 重做。正在輸入文字時歸文字框（文字框有自己的復原）。
    /// 回傳 true 表示已處理。
    /// </summary>
    private bool HandleHistoryKey(KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return false;
        var modifiers = Keyboard.Modifiers;
        var key = ShortcutKey(e);
        bool redo;
        if (key == Key.Z && modifiers == ModifierKeys.Control) redo = false;
        else if ((key == Key.Y && modifiers == ModifierKeys.Control) || (key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))) redo = true;
        else return false;
        StepHistory(redo);
        e.Handled = true;
        return true;
    }

    private void StepHistory(bool redo)
    {
        // 拖曳到一半時不動時間軸，否則拖曳中的片段和畫面會對不上。
        if (TimelineView.IsDragging || _framingDrag != FramingDragKind.None) return;
        _engine?.Pause();
        var label = redo ? _history.Redo() : _history.Undo();
        if (label == null) return;

        // 選取的片段被復原掉了（例如復原「加入片段」、分割出來的右段）就取消選取；
        // 還在的話重新整理右側的音量、旋轉、倒放與畫面位置，顯示復原後的值。
        var selected = TimelineView.SelectedClip;
        if (selected != null && !_timeline.AllClips.Contains(selected)) TimelineView.Select(null);
        else OnTimelineSelectionChanged(selected);
        OnClipsChanged();
        RelistMissingMedia();
        ShowStatus(redo ? $"已重做：{label}" : $"已復原：{label}");
    }

    /// <summary>
    /// 素材清單不在復原範圍內：復原「移除素材」連帶移除的片段後，片段回來了但素材不在清單裡，
    /// 沒辦法再預覽或標記。把這些素材加回清單（縮圖在背景重新產生，產生好才加入，因為 MediaItem 建立後不會更新畫面）。
    /// </summary>
    private async void RelistMissingMedia()
    {
        var missing = _timeline.AllClips.Select(c => c.Media).Distinct()
            .Where(info => _media.All(m => m.Info != info)).ToList();
        foreach (var info in missing)
        {
            byte[]? thumbnail = null;
            if (info.HasVideo && _extractor != null)
            {
                try
                {
                    thumbnail = await _extractor.ThumbnailAsync(info.Path, info.IsStill ? 0 : Math.Min(1, info.Duration / 10), 144, default, info.IsStill);
                }
                catch (Exception e) when (VideoEditor.Core.Ffmpeg.ExpectedErrors.IsProcess(e))
                {
                    // 沒有縮圖不影響其他功能。
                }
            }
            // 等縮圖的期間可能已經加回來（連按兩次復原）或片段又被移走。
            if (_media.Any(m => m.Info == info) || !_timeline.AllClips.Any(c => c.Media == info)) continue;
            _media.Add(new MediaItem(info) { Thumbnail = thumbnail is { Length: > 0 } ? TryCreateThumbnail(thumbnail) : null });
            UpdateMediaUsage();
        }
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.ToolTip = _history.UndoLabel is { } undo ? $"復原：{undo}（Ctrl+Z）" : "沒有可以復原的操作（Ctrl+Z）";
        RedoButton.ToolTip = _history.RedoLabel is { } redo ? $"重做：{redo}（Ctrl+Y）" : "沒有可以重做的操作（Ctrl+Y）";
    }
}
