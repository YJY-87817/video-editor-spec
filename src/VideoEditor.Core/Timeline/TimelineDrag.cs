namespace VideoEditor.Core.Timeline;

/// <summary>滑鼠按在片段上的哪個部分。</summary>
public enum ClipHit { Move, TrimStart, TrimEnd }

/// <summary>時間軸拖曳的規則。畫面只負責把滑鼠位置換算成座標與時間，判斷與編輯放在這裡，才能測試。</summary>
public static class TimelineDrag
{
    /// <summary>
    /// 滑鼠在片段左右緣之間（left 到 right，同單位）的 x 位置，按到的是左緣、右緣還是中間。
    /// 邊緣各佔 edgeGrab，片段太窄時最多各佔寬度的三分之一，中間仍可拖曳移動。
    /// </summary>
    public static ClipHit Classify(double x, double left, double right, double edgeGrab)
    {
        var grab = Math.Min(edgeGrab, (right - left) / 3);
        if (x - left <= grab) return ClipHit.TrimStart;
        if (right - x <= grab) return ClipHit.TrimEnd;
        return ClipHit.Move;
    }

    /// <summary>
    /// 拖曳移動片段：優先放在貼齊的位置 snapped。貼齊的位置放不下（開頭會小於 0，或同軌那裡已有片段）時，
    /// 片段會被推到別處，這時不算貼齊，改照滑鼠原本的位置 raw 移動，避免片段突然跳走。
    /// 回傳是否採用了貼齊的位置。
    /// </summary>
    public static bool MoveWithSnap(Timeline timeline, TimelineClip clip, double raw, double snapped)
    {
        var actual = timeline.Move(clip, snapped);
        if (Math.Abs(actual - snapped) <= 1e-6) return true;
        timeline.Move(clip, raw);
        return false;
    }
}
