namespace VideoEditor.Core.Models;

public enum FitMode
{
    /// <summary>填滿裁切</summary>
    Fill,
    /// <summary>加黑邊</summary>
    Pad,
}

public sealed record AspectRatio(int W, int H)
{
    public static readonly AspectRatio R16x9 = new(16, 9);
    public static readonly AspectRatio R9x16 = new(9, 16);
    public static readonly AspectRatio R1x1 = new(1, 1);
    public static readonly AspectRatio R4x5 = new(4, 5);
    public static readonly IReadOnlyList<AspectRatio> Presets = [R16x9, R9x16, R1x1, R4x5];

    public override string ToString() => $"{W}:{H}";
}

public sealed record OutputSettings(AspectRatio Ratio, FitMode Mode, int Crf = 18);
