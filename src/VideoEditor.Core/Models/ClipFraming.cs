namespace VideoEditor.Core.Models;

/// <summary>
/// 「填滿裁切」時，片段取用來源畫面的哪一部分（輸出框在來源上的位置與大小）。
/// Zoom 為 1 時是放得進來源的最大框（預設），越大框越小、畫面放大；
/// CenterX／CenterY 是框中心在來源畫面上的位置（0–1，0.5 為正中央）。
/// 座標以旋轉、鏡像後的畫面為準，也就是預覽上看到的方向。框一定完整落在來源內，不會出現黑邊。
/// </summary>
public sealed record ClipFraming(double Zoom, double CenterX, double CenterY)
{
    public const double MaxZoom = 8;
    private const double Tolerance = 1e-9;

    public static readonly ClipFraming Default = new(1, 0.5, 0.5);

    public bool IsDefault =>
        Math.Abs(Zoom - 1) < Tolerance && Math.Abs(CenterX - 0.5) < Tolerance && Math.Abs(CenterY - 0.5) < Tolerance;

    /// <summary>Zoom 為 1 時的框：輸出比例、放得進來源的最大尺寸。</summary>
    public static (double Width, double Height) LargestFrame(Size2 source, Size2 output)
    {
        var aspect = (double)output.Width / output.Height;
        return (double)source.Width / source.Height > aspect
            ? (source.Height * aspect, source.Height)
            : (source.Width, source.Width / aspect);
    }

    /// <summary>框在來源畫面上的位置（來源像素）。超出來源的部分會推回來源內。</summary>
    public Rect2 CropRect(Size2 source, Size2 output)
    {
        var (largestWidth, largestHeight) = LargestFrame(source, output);
        var zoom = Math.Clamp(Zoom, 1, MaxZoom);
        var width = largestWidth / zoom;
        var height = largestHeight / zoom;
        var x = Math.Clamp(CenterX * source.Width - width / 2, 0, source.Width - width);
        var y = Math.Clamp(CenterY * source.Height - height / 2, 0, source.Height - height);
        return new Rect2(x, y, width, height);
    }

    /// <summary>由框在來源上的位置換算回 ClipFraming。框的長寬比以輸出為準，只看寬度。</summary>
    public static ClipFraming FromCrop(Rect2 crop, Size2 source, Size2 output)
    {
        var (largestWidth, _) = LargestFrame(source, output);
        var zoom = Math.Clamp(largestWidth / Math.Max(crop.Width, Tolerance), 1, MaxZoom);
        var framing = new ClipFraming(zoom, (crop.X + crop.Width / 2) / source.Width, (crop.Y + crop.Height / 2) / source.Height);
        return framing.Normalize(source, output);
    }

    /// <summary>把推回來源內之後的實際位置存回來，之後拖曳才不會有「拉過頭、拉回來沒反應」的空檔。</summary>
    public ClipFraming Normalize(Size2 source, Size2 output)
    {
        var crop = CropRect(source, output);
        var (largestWidth, _) = LargestFrame(source, output);
        return new ClipFraming(largestWidth / crop.Width, (crop.X + crop.Width / 2) / source.Width, (crop.Y + crop.Height / 2) / source.Height);
    }

    /// <summary>整個框移動 (dx, dy) 來源像素。</summary>
    public ClipFraming MoveBy(double dx, double dy, Size2 source, Size2 output) =>
        (this with { CenterX = CenterX + dx / source.Width, CenterY = CenterY + dy / source.Height }).Normalize(source, output);

    /// <summary>以框中心為準縮放，factor 大於 1 放大畫面（框變小）。</summary>
    public ClipFraming ZoomBy(double factor, Size2 source, Size2 output) =>
        (this with { Zoom = Math.Clamp(Zoom * factor, 1, MaxZoom) }).Normalize(source, output);

    /// <summary>
    /// 拖曳框的一角調整大小：對角 (anchorX, anchorY) 固定，拖曳的角往 (directionX, directionY)（各為 +1 或 -1）延伸，
    /// 跟著游標 (pointerX, pointerY)。維持輸出比例，大小限制在 Zoom 1–MaxZoom，且不超出來源。
    /// </summary>
    public static ClipFraming ResizeFromCorner(double anchorX, double anchorY, int directionX, int directionY,
        double pointerX, double pointerY, Size2 source, Size2 output)
    {
        var (largestWidth, _) = LargestFrame(source, output);
        var aspect = (double)output.Width / output.Height;
        var width = Math.Max((pointerX - anchorX) * directionX, (pointerY - anchorY) * directionY * aspect);
        // 往拖曳方向還有多少空間。
        var roomX = directionX > 0 ? source.Width - anchorX : anchorX;
        var roomY = (directionY > 0 ? source.Height - anchorY : anchorY) * aspect;
        width = Math.Clamp(width, largestWidth / MaxZoom, Math.Max(largestWidth / MaxZoom, Math.Min(largestWidth, Math.Min(roomX, roomY))));
        var height = width / aspect;
        var x = directionX > 0 ? anchorX : anchorX - width;
        var y = directionY > 0 ? anchorY : anchorY - height;
        return FromCrop(new Rect2(x, y, width, height), source, output);
    }

    /// <summary>
    /// FFmpeg 的 crop 濾鏡，以來源寬高的比例表示，結尾含逗號；預設位置時為空字串。
    /// 用比例而非像素，像素比修正與旋轉後的實際尺寸有一像素的出入時也不會裁錯。
    /// </summary>
    public string CropFilter(Size2 source, Size2 output)
    {
        if (IsDefault) return "";
        var crop = CropRect(source, output);
        return $"crop=w=iw*{Num(crop.Width / source.Width)}:h=ih*{Num(crop.Height / source.Height)}"
            + $":x=iw*{Num(crop.X / source.Width)}:y=ih*{Num(crop.Y / source.Height)},";
    }

    public string Label => IsDefault ? "預設（置中、最大）" : $"放大 {Zoom * 100:0}%";

    private static string Num(double value) => value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
}
