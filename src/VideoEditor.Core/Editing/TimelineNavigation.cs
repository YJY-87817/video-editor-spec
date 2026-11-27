namespace VideoEditor.Core.Editing;

/// <summary>用鍵盤在時間軸上移動選取（Alt+←／→，UIUX-PLAN-2 #5）。</summary>
public static class TimelineNavigation
{
    /// <summary>
    /// 選取的片段的前一段（direction = -1）或後一段（1），在同一軌上找；已經是第一段或最後一段時回傳 null。
    /// 沒有選取時：播放頭在某一段上就選那一段，否則往指定的方向找最近的一段。
    /// </summary>
    public static TimelineClip? Adjacent(IReadOnlyList<TimelineClip> clips, TimelineClip? selected, double playhead, int direction)
    {
        if (clips.Count == 0) return null;
        if (selected != null)
        {
            var index = IndexOf(clips, selected);
            if (index >= 0)
            {
                var next = index + Math.Sign(direction);
                return next >= 0 && next < clips.Count ? clips[next] : null;
            }
        }
        var under = clips.FirstOrDefault(c => playhead >= c.Start && playhead < c.End);
        if (under != null) return under;
        return direction > 0
            ? clips.FirstOrDefault(c => c.Start >= playhead)
            : clips.LastOrDefault(c => c.End <= playhead);
    }

    private static int IndexOf(IReadOnlyList<TimelineClip> clips, TimelineClip clip)
    {
        for (var i = 0; i < clips.Count; i++)
            if (clips[i] == clip) return i;
        return -1;
    }
}
