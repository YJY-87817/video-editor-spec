using System.Buffers.Binary;

namespace VideoEditor.Core.Models;

/// <summary>
/// 素材的聲音波形：每 <see cref="SecondsPerPeak"/> 秒一個峰值（0–1 的線性振幅，取各聲道絕對值的最大值）。
/// 另外存每 2、4、8…格合併的最大值，任何長度的區間都只要查 O(log n) 個數，
/// 時間軸縮到很小、一像素涵蓋上千格時，播放中整條重畫也不會變慢。
/// </summary>
public sealed class WaveformData
{
    public const int PeaksPerSecond = 500;
    public const double SecondsPerPeak = 1.0 / PeaksPerSecond;
    /// <summary>波形高度的下限：低於這個音量畫成一條線。</summary>
    public const double FloorDb = -48;

    // _levels[0] 是原始峰值；_levels[k][j] = _levels[k-1][2j] 與 [2j+1] 的最大值，一直到只剩一個。
    private readonly float[][] _levels;

    public WaveformData(float[] peaks)
    {
        var levels = new List<float[]> { peaks };
        while (levels[^1].Length > 1)
        {
            var below = levels[^1];
            var level = new float[(below.Length + 1) / 2];
            for (var j = 0; j < level.Length; j++)
                level[j] = 2 * j + 1 < below.Length ? Math.Max(below[2 * j], below[2 * j + 1]) : below[2 * j];
            levels.Add(level);
        }
        _levels = levels.ToArray();
    }

    public int Count => _levels[0].Length;
    public double Duration => Count * SecondsPerPeak;

    /// <summary>來源時間 [from, to) 之間的最大峰值。區間比一格還短時取所在的那一格；超出素材範圍的部分當成靜音。</summary>
    public float Peak(double from, double to)
    {
        if (to < from) (from, to) = (to, from);
        // 容許浮點誤差：剛好落在格線上的時間不要多算進前一格或後一格。
        var lo = (long)Math.Floor(from * PeaksPerSecond + 1e-6);
        var hi = (long)Math.Ceiling(to * PeaksPerSecond - 1e-6);
        if (hi <= lo) hi = lo + 1;
        lo = Math.Max(0, lo);
        hi = Math.Min(Count, hi);
        if (lo >= hi) return 0;

        // 由下往上：兩端不成對的那一格先算進去，其餘的交給上一層。
        var max = 0f;
        var low = (int)lo;
        var high = (int)hi;
        for (var k = 0; low < high; k++)
        {
            var level = _levels[k];
            if ((low & 1) == 1) max = Math.Max(max, level[low++]);
            if ((high & 1) == 1) max = Math.Max(max, level[--high]);
            low >>= 1;
            high >>= 1;
        }
        return max;
    }

    /// <summary>峰值乘上音量後換成波形半高的比例（0–1）：<see cref="FloorDb"/> 到 0 dB 線性對應，超過 0 dB 以 1 計。</summary>
    public static double Level(double peak, double volume)
    {
        var amplitude = peak * volume;
        if (amplitude <= 0) return 0;
        var db = 20 * Math.Log10(amplitude);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>乘上音量後超過 0 dB：匯出是線性增益、沒有限幅，這裡會破音。</summary>
    public static bool Clips(double peak, double volume) => peak * volume > 1.0 + 1e-6;
}

/// <summary>把 16-bit 交錯 PCM 逐格累積成 <see cref="WaveformData"/>。資料可以任意切塊餵進來。</summary>
public sealed class WaveformBuilder
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly List<float> _peaks = [];
    private readonly byte[] _partial;
    private int _partialCount;
    private long _samples;
    private float _current;

    public WaveformBuilder(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _sampleRate = sampleRate;
        _channels = channels;
        _partial = new byte[channels * 2];
    }

    /// <summary>加入 s16le 交錯資料；結尾不足一個取樣點的位元組留到下一塊。</summary>
    public void Add(ReadOnlySpan<byte> pcm)
    {
        var frameBytes = _partial.Length;
        if (_partialCount > 0)
        {
            var take = Math.Min(frameBytes - _partialCount, pcm.Length);
            pcm[..take].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            pcm = pcm[take..];
            if (_partialCount < frameBytes) return;
            AddFrame(_partial);
            _partialCount = 0;
        }
        while (pcm.Length >= frameBytes)
        {
            AddFrame(pcm[..frameBytes]);
            pcm = pcm[frameBytes..];
        }
        pcm.CopyTo(_partial);
        _partialCount = pcm.Length;
    }

    private void AddFrame(ReadOnlySpan<byte> frame)
    {
        var max = 0;
        for (var c = 0; c < _channels; c++)
            max = Math.Max(max, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(frame[(c * 2)..])));
        AddSample(max / 32768f);
    }

    /// <summary>加入一個取樣點的振幅（已取各聲道最大值，0–1）。</summary>
    public void AddSample(float amplitude)
    {
        var bucket = _samples * WaveformData.PeaksPerSecond / _sampleRate;
        while (_peaks.Count < bucket)
        {
            _peaks.Add(_current);
            _current = 0;
        }
        _current = Math.Max(_current, amplitude);
        _samples++;
    }

    public WaveformData Build()
    {
        var peaks = new List<float>(_peaks);
        if (_samples > 0) peaks.Add(_current);
        return new WaveformData(peaks.ToArray());
    }
}
