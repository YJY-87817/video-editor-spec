using System.Windows;
using System.Windows.Input;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 帶修飾鍵的編輯快捷鍵與相關操作（UIUX-PLAN-2 階段 3） ----

    /// <summary>
    /// Shift+E 插入到播放頭、Shift+Delete 刪除並補齊、Alt+←／→ 選前後片段、+／- 縮放時間軸。
    /// 正在輸入文字時歸文字框。回傳 true 表示已處理。
    /// </summary>
    private bool HandleEditKey(KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return false;
        var modifiers = Keyboard.Modifiers;
        var key = ShortcutKey(e);
        switch (key)
        {
            case Key.E when modifiers == ModifierKeys.Shift:
                InsertAtPlayhead();
                break;
            case Key.Delete when modifiers == ModifierKeys.Shift && !MediaList.IsKeyboardFocusWithin:
                RemoveSelectedAndClose();
                break;
            case Key.Left or Key.Right when modifiers == ModifierKeys.Alt:
                SelectAdjacentClip(key == Key.Right ? 1 : -1);
                break;
            // 主鍵盤的 + 要按 Shift（= 鍵），數字鍵盤不用；兩種都接受。
            case Key.OemPlus or Key.Add when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                OnZoomIn(this, new RoutedEventArgs());
                break;
            case Key.OemMinus or Key.Subtract when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                OnZoomOut(this, new RoutedEventArgs());
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>Home／End：剪輯預覽時移動時間軸播放頭；匯入影片預覽時移動素材的位置。結尾停在最後一格，畫面才不會是黑的。</summary>
    private void GoToEnd(bool atStart)
    {
        if (_engine == null) return;
        var fps = CurrentFps();
        var time = atStart ? 0 : Math.Max(0, Timecode.AlignToFrame(_engine.Duration - 1 / fps, fps));
        if (IsTimelinePreview) TimelineView.Playhead = time;
        _engine.Seek(time);
    }

    private void OnInsertAtPlayheadClick(object sender, RoutedEventArgs e) => InsertAtPlayhead();

    /// <summary>
    /// Shift+E：把預覽中的素材（含入出點）插入到時間軸播放頭的位置，規則和拖曳插入相同：
    /// 播放頭在片段前半就插在它前面，後半就插在它後面，同一軌後面的片段往後移。
    /// </summary>
    private void InsertAtPlayhead()
    {
        if (_selectedMedia == null)
        {
            ShowStatus("先在素材清單選好要插入的素材。", StatusKind.Warning);
            return;
        }
        OnMediaDropped(_selectedMedia.Info, TimelineView.Playhead);
    }

    private void OnRippleDeleteClick(object sender, RoutedEventArgs e) => RemoveSelectedAndClose();

    /// <summary>Shift+Delete：刪除選取的片段，同一軌後面的片段往前補上（配樂軌不受影片軌影響）。</summary>
    private void RemoveSelectedAndClose()
    {
        if (TimelineView.SelectedClip is not { } clip) return;
        _engine?.Pause();
        var before = _history.Capture();
        var moved = _timeline.RemoveAndClose(clip);
        _history.Commit(before, "刪除並補齊");
        TimelineView.Select(null);
        OnClipsChanged();
        ShowStatus(moved == 0 ? "已刪除片段" : $"已刪除片段，後面 {moved} 段往前移 {clip.Duration:0.##} 秒（Ctrl+Z 可復原）", StatusKind.Success);
    }

    /// <summary>Alt+←／→：在同一軌上選取前一段或後一段，播放頭移到那段的開頭，並切到剪輯預覽。</summary>
    private void SelectAdjacentClip(int direction)
    {
        var selected = TimelineView.SelectedClip;
        var clips = selected?.Track == TrackKind.Music ? _timeline.MusicClips : _timeline.VideoClips;
        if (TimelineNavigation.Adjacent(clips, selected, TimelineView.Playhead, direction) is not { } target) return;
        TimelineView.Select(target);
        SwitchToTimelinePreview();
        TimelineView.Playhead = target.Start;
        _engine?.Seek(target.Start);
    }
}
