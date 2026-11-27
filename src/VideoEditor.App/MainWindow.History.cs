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
        bool redo;
        if (e.Key == Key.Z && modifiers == ModifierKeys.Control) redo = false;
        else if ((e.Key == Key.Y && modifiers == ModifierKeys.Control) || (e.Key == Key.Z && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))) redo = true;
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
        ShowStatus(redo ? $"已重做：{label}" : $"已復原：{label}");
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.ToolTip = _history.UndoLabel is { } undo ? $"復原：{undo}（Ctrl+Z）" : "沒有可以復原的操作（Ctrl+Z）";
        RedoButton.ToolTip = _history.RedoLabel is { } redo ? $"重做：{redo}（Ctrl+Y）" : "沒有可以重做的操作（Ctrl+Y）";
    }
}
