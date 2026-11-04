namespace VideoEditor.Core.Models;

/// <summary>
/// 片段畫面的旋轉與鏡像。先順時針旋轉 Rotation 度，再對旋轉後的畫面做水平／垂直鏡像，
/// 所以鏡像的方向永遠是使用者在畫面上看到的方向。
/// </summary>
public readonly record struct FrameTransform(int Rotation = 0, bool FlipHorizontal = false, bool FlipVertical = false)
{
    public static readonly FrameTransform Identity = new();

    public bool IsIdentity => Rotation == 0 && !FlipHorizontal && !FlipVertical;
    /// <summary>旋轉 90° 或 270° 時，畫面的長寬互換。</summary>
    public bool SwapsSize => Rotation is 90 or 270;

    /// <summary>再順時針轉 degrees 度（可為負值）。已鏡像的畫面，旋轉方向依畫面上看到的為準。</summary>
    public FrameTransform Rotate(int degrees)
    {
        // 鏡像奇數次時，畫面上的順時針等於原畫面的逆時針。
        var mirrored = FlipHorizontal != FlipVertical;
        var delta = mirrored ? -degrees : degrees;
        return this with { Rotation = Normalize(Rotation + delta) };
    }

    public FrameTransform ToggleHorizontal() => this with { FlipHorizontal = !FlipHorizontal };
    public FrameTransform ToggleVertical() => this with { FlipVertical = !FlipVertical };

    public Size2 Apply(Size2 size) => SwapsSize ? new Size2(size.Height, size.Width) : size;

    /// <summary>FFmpeg 濾鏡，結尾含逗號；不需轉換時為空字串。transpose 是無損的像素搬移，不影響畫質。</summary>
    public string Filter()
    {
        var filters = new List<string>();
        switch (Rotation)
        {
            case 90: filters.Add("transpose=clock"); break;
            case 180: filters.Add("hflip"); filters.Add("vflip"); break;
            case 270: filters.Add("transpose=cclock"); break;
        }
        if (FlipHorizontal) filters.Add("hflip");
        if (FlipVertical) filters.Add("vflip");
        return filters.Count == 0 ? "" : string.Join(',', filters) + ",";
    }

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (Rotation != 0) parts.Add($"↻{Rotation}°");
            if (FlipHorizontal) parts.Add("水平鏡像");
            if (FlipVertical) parts.Add("垂直鏡像");
            return parts.Count == 0 ? "原始方向" : string.Join("、", parts);
        }
    }

    private static int Normalize(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;
}
