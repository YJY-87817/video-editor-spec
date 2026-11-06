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
}
