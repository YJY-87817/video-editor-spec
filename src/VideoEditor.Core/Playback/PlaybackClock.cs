namespace VideoEditor.Core.Playback;

/// <summary>預覽的時鐘與同步規則。純計算，與 LibVLC、計時器無關，方便測試。</summary>
public static class PlaybackClock
{
    /// <summary>
    /// 播放位置：從 clockBase 起算，經過 elapsedSeconds 秒、倍速 rate。
    /// 新片段的第一格還沒出現時（waitingForFirstFrame）時鐘不走，停在 clockBase。
    /// </summary>
    public static double Position(double clockBase, double elapsedSeconds, int rate, bool waitingForFirstFrame) =>
        waitingForFirstFrame ? clockBase : clockBase + elapsedSeconds * rate;

    /// <summary>倒帶的位置：從 rewindBase 往回，每秒退 speed 秒。可能是負數，由呼叫端判斷是否已到開頭。</summary>
    public static double RewindPosition(double rewindBase, double elapsedSeconds, int speed) =>
        rewindBase - elapsedSeconds * speed;

    /// <summary>
    /// 播放器實際的進度（actualMs）與時鐘預期的進度（expectedMs）差距是否太大。
    /// 倍速時同樣的延遲會造成較大的來源時間差，容許值（limitMs）跟著倍數放大。
    /// </summary>
    public static bool DriftExceeded(double actualMs, double expectedMs, double limitMs, int rate) =>
        Math.Abs(actualMs - expectedMs) > limitMs * rate;
}
