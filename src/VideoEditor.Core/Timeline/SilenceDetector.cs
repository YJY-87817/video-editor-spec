using System.Runtime.CompilerServices;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Timeline;

/// <summary>來源時間上的一段區間（秒）。</summary>
public readonly record struct TimeRange(double From, double To)
{
    public double Length => To - From;
}

/// <summary>
/// 從波形找出靜音的段落，給修剪時吸附用。
/// 連續一段峰值都低於 <see cref="ThresholdDb"/>、長度達到 <see cref="MinDuration"/> 才算靜音；
/// 字與字之間的短停頓不算，吸附點才不會太密。用素材的原始聲音判斷，不乘上片段音量。
/// </summary>
public static class SilenceDetector
{
    /// <summary>峰值低於這個音量算安靜。一般錄音的背景噪音大多在 -50 到 -45 dB。</summary>
    public const double ThresholdDb = -40;
    /// <summary>安靜至少這麼久才算停頓。</summary>
    public const double MinDuration = 0.25;

    // 每份波形只算一次；波形不在了（素材移除、程式關閉）快取跟著消失。
    private static readonly ConditionalWeakTable<WaveformData, IReadOnlyList<TimeRange>> Cache = new();

    /// <summary>用預設門檻與最短長度找出的靜音區間（依時間排序），結果會快取。</summary>
    public static IReadOnlyList<TimeRange> Find(WaveformData data) => Cache.GetValue(data, d => Detect(d));

    public static IReadOnlyList<TimeRange> Detect(WaveformData data, double thresholdDb = ThresholdDb, double minDuration = MinDuration)
    {
        var threshold = (float)Math.Pow(10, thresholdDb / 20);
        var minPeaks = (int)Math.Ceiling(minDuration * WaveformData.PeaksPerSecond - 1e-6);
        var ranges = new List<TimeRange>();
        var runStart = -1;
        for (var i = 0; i <= data.Count; i++)
        {
            var quiet = i < data.Count && data[i] < threshold;
            if (quiet)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0 && i - runStart >= minPeaks)
                ranges.Add(new TimeRange(runStart * WaveformData.SecondsPerPeak, i * WaveformData.SecondsPerPeak));
            runStart = -1;
        }
        return ranges;
    }
}
