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
    /// <summary>原尺寸：沿用基準素材的長寬，不依比例計算。</summary>
    public static readonly AspectRatio Original = new(0, 0);
    public static readonly IReadOnlyList<AspectRatio> Presets = [R16x9, R9x16, R1x1, R4x5];

    public bool IsOriginal => W == 0 || H == 0;

    public override string ToString() => IsOriginal ? "原尺寸" : $"{W}:{H}";
}

public sealed record OutputSettings(AspectRatio Ratio, FitMode Mode, int Crf = 18);
