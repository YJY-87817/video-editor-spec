namespace VideoEditor.Core.Models;

public readonly record struct Size2(int Width, int Height);
public readonly record struct Rect2(double X, double Y, double Width, double Height);

public static class OutputSize
{
    /// <summary>以來源較大邊為基準，較小邊依目標比例計算並取偶數。</summary>
    public static Size2 Compute(int sourceWidth, int sourceHeight, AspectRatio ratio)
    {
        if (ratio.IsOriginal) return new Size2(MakeEven(sourceWidth), MakeEven(sourceHeight));

        var longSide = MakeEven(Math.Max(sourceWidth, sourceHeight));
        if (ratio.W >= ratio.H)
            return new Size2(longSide, MakeEven((double)longSide * ratio.H / ratio.W));
        return new Size2(MakeEven((double)longSide * ratio.W / ratio.H), longSide);
    }

    private static int MakeEven(double value) => (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2;
}

/// <summary>預覽區內，來源畫面與輸出框線各自的位置。</summary>
public readonly record struct PreviewLayout(Rect2 Source, Rect2 Frame)
{
    public static PreviewLayout Compute(double areaWidth, double areaHeight, Size2 source, Size2 output, FitMode mode)
    {
        if (mode == FitMode.Fill)
        {
            // 完整顯示來源，框線內縮，框外即會被裁掉的範圍。
            var sourceRect = Fit(areaWidth, areaHeight, 0, 0, source.Width, source.Height);
            return new PreviewLayout(sourceRect, Fit(sourceRect.Width, sourceRect.Height, sourceRect.X, sourceRect.Y, output.Width, output.Height));
        }

        var frame = Fit(areaWidth, areaHeight, 0, 0, output.Width, output.Height);
        return new PreviewLayout(Fit(frame.Width, frame.Height, frame.X, frame.Y, source.Width, source.Height), frame);
    }

    private static Rect2 Fit(double boxWidth, double boxHeight, double boxX, double boxY, int width, int height)
    {
        var scale = Math.Min(boxWidth / width, boxHeight / height);
        var w = width * scale;
        var h = height * scale;
        return new Rect2(boxX + (boxWidth - w) / 2, boxY + (boxHeight - h) / 2, w, h);
    }
}

public static class VolumeCurve
{
    public const double MaxGain = 2.0;

    /// <summary>
    /// 線性增益轉 LibVLC 音量。LibVLC 的 Windows 音訊輸出（mmdevice）以音量值的三次方作為增益，
    /// 因此取立方根，讓預覽與匯出的 volume 濾鏡使用相同的線性增益。
    /// </summary>
    public static int ToVlcVolume(double gain) =>
        (int)Math.Round(100 * Math.Cbrt(Math.Clamp(gain, 0, MaxGain)));
}
