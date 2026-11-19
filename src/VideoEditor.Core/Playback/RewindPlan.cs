namespace VideoEditor.Core.Playback;

/// <summary>
/// 倒帶預覽的影格挑選。倒帶是 FFmpeg 成批解出影格放進快取，再依時鐘顯示；這裡決定顯示哪一格、接下來解哪一批。
/// 高倍速時每 step 格只解一格。direction 是影格編號隨時間軸往回走的方向：
/// 一般片段 -1（來源時間變小），倒放片段 +1。
/// </summary>
public static class RewindPlan
{
    /// <summary>
    /// 要顯示的影格：從 frame 起，往「剛經過」的方向最多找 step 格，回傳第一個已經在快取的；都沒有則為 null。
    /// 高倍速只解每 step 格中的一格，所以要找最近的一格。
    /// </summary>
    public static long? NearestCached(long frame, int direction, int step, Func<long, bool> isCached)
    {
        for (var d = 0; d < step; d++)
        {
            var candidate = frame - d * direction;
            if (isCached(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 下一批要解的起點：從 frame 起每 step 格取一個、最多看 lookahead 個，回傳第一個還不知道結果的（沒有在快取，也沒被標成解不出來）。
    /// 走出片段範圍 [first, last] 就停止，沒有需要解的回傳 null。
    /// </summary>
    public static long? NextFetchTarget(long frame, int direction, int step, long first, long last, int lookahead, Func<long, bool> isKnown)
    {
        for (var i = 0; i < lookahead; i++)
        {
            var target = frame + (long)i * step * direction;
            if (target < first || target > last) return null;
            if (isKnown(target)) continue;
            return target;
        }
        return null;
    }
}
