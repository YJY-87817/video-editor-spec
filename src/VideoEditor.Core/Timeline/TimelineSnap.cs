namespace VideoEditor.Core.Timeline;

/// <summary>拉動片段邊緣時的吸附規則。</summary>
public static class TimelineSnap
{
    /// <summary>
    /// 時間在播放頭前後 tolerance 秒以內時，改成播放頭的時間，讓剪輯點能準確對齊預覽的位置。
    /// tolerance 由畫面上的像素距離換算，所以時間軸放大時吸附範圍跟著變小。
    /// </summary>
    public static (double Time, bool Snapped) ToPlayhead(double time, double playhead, double tolerance) =>
        tolerance > 0 && Math.Abs(time - playhead) <= tolerance ? (playhead, true) : (time, false);

    /// <summary>
    /// 吸附後，邊緣是否真的落在播放頭上（用來決定要不要顯示「已貼齊」）。
    /// 修剪的切點會對齊來源影格，所以邊緣與播放頭最多可以差半格；
    /// 差得更多表示邊緣被來源長度或相鄰片段擋住，沒有貼到。沒有影格率的素材（純音訊）不對齊，要求完全相同。
    /// </summary>
    public static bool IsAtPlayhead(double edge, double playhead, double fps) =>
        Math.Abs(edge - playhead) <= (fps > 0 ? 0.5 / fps : 0) + 1e-6;
}
