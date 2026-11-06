namespace VideoEditor.Core.Models;

/// <summary>預覽區目前顯示的畫面。沒有畫面時用 null；只有音訊的素材 VideoSize 為 null。</summary>
public readonly record struct PreviewSurface(Size2? VideoSize, FrameTransform Transform);

/// <summary>
/// 預覽區排版結果。Image 是未旋轉前的畫面位置與大小（旋轉 90°／270° 時長寬已互換，
/// 轉完剛好落在 Source 內）。
/// </summary>
public readonly record struct PreviewPlan(bool Visible, bool ShowImage, Rect2 Source, Rect2 Frame, Rect2 Image, bool DimCrop)
{
    public static readonly PreviewPlan Hidden = new(false, false, default, default, default, false);
}

/// <summary>依目前的素材、比例與模式，算出來源畫面與輸出框線在預覽區內的位置。不依賴任何畫面元件。</summary>
public static class PreviewPlanner
{
    /// <param name="reference">決定輸出尺寸的畫面（含旋轉）；沒有時整個預覽收起。</param>
    /// <param name="surface">目前顯示的畫面；時間軸空白處沒有來源畫面，用輸出尺寸排版，只顯示黑色的輸出框。</param>
    public static PreviewPlan Compute(double areaWidth, double areaHeight, Size2? reference, AspectRatio ratio, FitMode mode,
        PreviewSurface? surface)
    {
        if (reference == null || areaWidth <= 0 || areaHeight <= 0) return PreviewPlan.Hidden;

        var output = OutputSize.Compute(reference.Value.Width, reference.Value.Height, ratio);
        var transform = surface?.Transform ?? FrameTransform.Identity;
        var source = surface?.VideoSize is { } size ? transform.Apply(size) : output;
        var layout = PreviewLayout.Compute(areaWidth, areaHeight, source, output, mode);

        // 填滿裁切：把框外（會被裁掉的範圍）調暗。
        return new PreviewPlan(true, surface != null, layout.Source, layout.Frame, ImageRect(layout.Source, transform),
            mode == FitMode.Fill && surface != null);
    }

    /// <summary>
    /// 預覽畫面以原始方向放置，再以中心為原點旋轉與鏡像，轉完後剛好落在 rect 內，
    /// 所以旋轉 90°／270° 時長寬要先互換。
    /// </summary>
    public static Rect2 ImageRect(Rect2 rect, FrameTransform transform)
    {
        var (width, height) = transform.SwapsSize ? (rect.Height, rect.Width) : (rect.Width, rect.Height);
        return new Rect2(rect.X + (rect.Width - width) / 2, rect.Y + (rect.Height - height) / 2, width, height);
    }
}
