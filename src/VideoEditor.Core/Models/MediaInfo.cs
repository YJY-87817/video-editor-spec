namespace VideoEditor.Core.Models;

public sealed record Rational(int Num, int Den)
{
    public double Value => Den == 0 ? 0 : (double)Num / Den;
    public override string ToString() => $"{Num}/{Den}";

    public static Rational Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new Rational(0, 1);
        var parts = text.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out var n) && int.TryParse(parts[1], out var d))
            return new Rational(n, d);
        return int.TryParse(text, out var whole) ? new Rational(whole, 1) : new Rational(0, 1);
    }
}

/// <summary>ffprobe 讀到的素材資訊。Display 尺寸已計入旋轉與像素比。</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }
    public double Duration { get; init; }
    public long BitRate { get; init; }

    public bool HasVideo { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public int Rotation { get; init; }
    public Rational FrameRate { get; init; } = new(0, 1);
    public bool IsVariableFrameRate { get; init; }
    public string VideoCodec { get; init; } = "";
    public string PixelFormat { get; init; } = "";
    public int BitDepth { get; init; } = 8;
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    public Rational SampleAspect { get; init; } = new(1, 1);

    public bool HasAudio { get; init; }
    public string AudioCodec { get; init; } = "";
    public long AudioBitRate { get; init; }
    public int AudioSampleRate { get; init; }
    public int AudioChannels { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    public string AspectLabel
    {
        get
        {
            if (DisplayWidth <= 0 || DisplayHeight <= 0) return "—";
            var gcd = Gcd(DisplayWidth, DisplayHeight);
            return $"{DisplayWidth / gcd}:{DisplayHeight / gcd}";
        }
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
