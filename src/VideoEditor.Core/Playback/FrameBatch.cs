namespace VideoEditor.Core.Playback;

/// <summary>
/// 逐格移動時一次解出的影格範圍。長 GOP 的來源每解一格都要從前一個關鍵影格解起，
/// 一次多解幾格放進快取，之後連續按方向鍵就不必每一步都重新解碼。
/// 影格編號都是來源的影格順序；direction 為 +1（往後）或 -1（往前）。
/// </summary>
public static class FrameBatch
{
    /// <summary>
    /// 包含 frame、往 direction 方向延伸最多 size 格的範圍，限制在 [first, last] 內。
    /// 回傳由小到大的起點與格數；frame 不在範圍內時回傳 null。
    /// </summary>
    public static (long Start, int Count)? Toward(long frame, int direction, long first, long last, int size)
    {
        if (size <= 0 || frame < first || frame > last) return null;
        if (direction >= 0) return (frame, (int)Math.Min(size, last - frame + 1));
        var start = Math.Max(first, frame - (size - 1));
        return (start, (int)(frame - start + 1));
    }

    /// <summary>
    /// 往 direction 方向 lead 格以內，第一個還沒解出的影格；都已解出或超出 [first, last] 時回傳 null。
    /// 用來在快取快用完之前先解下一批。
    /// </summary>
    public static long? FirstMissing(long frame, int direction, long first, long last, int lead, Func<long, bool> isAvailable)
    {
        var sign = Math.Sign(direction);
        if (sign == 0) return null;
        for (var i = 1; i <= lead; i++)
        {
            var target = frame + i * sign;
            if (target < first || target > last) return null;
            if (!isAvailable(target)) return target;
        }
        return null;
    }
}
