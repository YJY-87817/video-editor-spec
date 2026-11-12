using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class SilenceDetectorTests
{
    private const float Loud = 0.5f;
    private const float Quiet = 0.005f;   // 約 -46 dB，低於 -40 dB 的門檻

    /// <summary>依序把每段（秒數, 峰值）展開成波形。</summary>
    private static WaveformData Wave(params (double Seconds, float Peak)[] parts) =>
        new(parts.SelectMany(p => Enumerable.Repeat(p.Peak, (int)Math.Round(p.Seconds * WaveformData.PeaksPerSecond))).ToArray());

    [Fact]
    public void FindsPausesIncludingHeadAndTail()
    {
        var silences = SilenceDetector.Detect(Wave((1, Quiet), (2, Loud), (0.5, 0), (1, Loud), (0.3, Quiet)));
        Assert.Equal([new TimeRange(0, 1), new TimeRange(3, 3.5), new TimeRange(4.5, 4.8)], silences.Select(Round));
    }

    [Fact]
    public void ShortPausesAreNotSilence()
    {
        var silences = SilenceDetector.Detect(Wave((1, Loud), (0.2, 0), (1, Loud), (0.25, 0), (1, Loud)));
        Assert.Equal([new TimeRange(2.2, 2.45)], silences.Select(Round));
    }

    [Fact]
    public void ThresholdIsMinus40Db()
    {
        Assert.Single(SilenceDetector.Detect(Wave((1, 0.0099f))));
        Assert.Empty(SilenceDetector.Detect(Wave((1, 0.0101f))));
    }

    [Fact]
    public void AllSilentNoSilenceAndEmpty()
    {
        Assert.Equal([new TimeRange(0, 2)], SilenceDetector.Detect(Wave((2, 0))).Select(Round));
        Assert.Empty(SilenceDetector.Detect(Wave((2, Loud))));
        Assert.Empty(SilenceDetector.Detect(new WaveformData([])));
    }

    [Fact]
    public void ResultIsCachedPerWaveform()
    {
        var data = Wave((1, 0), (1, Loud));
        Assert.Same(SilenceDetector.Find(data), SilenceDetector.Find(data));
    }

    private static TimeRange Round(TimeRange r) => new(Math.Round(r.From, 6), Math.Round(r.To, 6));
}

public class SilenceSnapTests
{
    private static MediaInfo Media(int fps = 30) => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(fps, 1), PixelFormat = "yuv420p",
    };

    // 來源 1–2 秒、5–8 秒是靜音。
    private static readonly TimeRange[] Silences = [new(1, 2), new(5, 8)];

    [Fact]
    public void LeftEdgeSnapsBeforeSoundStarts()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0.5, 9);
        t.Move(clip, 10);
        // 來源 s 在時間軸上是 10 + (s - 0.5)。
        Assert.Equal([11.4, 17.4], TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.Start).Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void RightEdgeSnapsAfterSoundEnds()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0.5, 9);
        Assert.Equal([0.6, 4.6], TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.End).Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void ReversedClipUsesTimelineDirection()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0.5, 9);
        t.SetReversed(clip, true);
        // 倒放：來源 s 在時間軸上是 9 - s，靜音在 1–4 與 7–8 秒。
        Assert.Equal([3.9, 7.9], TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.Start).Select(x => Math.Round(x, 9)));
        Assert.Equal([1.1, 7.1], TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.End).Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void ShortSilenceUsesItsMiddle()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 10);
        Assert.Equal([3.05], TimelineSnap.SilenceTargets(clip, [new TimeRange(3, 3.1)], TrimEdge.Start).Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void SilenceOutsideTheCurrentClipIsAlsoATarget()
    {
        // 片段只用來源 3–4 秒，往左拉長時仍可吸附到 1–2 秒那段靜音。
        var t = new TimelineModel();
        var clip = t.Append(Media(), 3, 4);
        t.Move(clip, 5);
        Assert.Equal(3.9, TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.Start)[0], 9);
    }

    [Theory]
    [InlineData(5.05, 5.0, 0.2, 5.0, SnapKind.Playhead)]   // 只有播放頭在範圍內
    [InlineData(3.05, 9.0, 0.2, 3.0, SnapKind.Silence)]    // 只有靜音在範圍內
    [InlineData(4.95, 5.1, 0.2, 4.9, SnapKind.Silence)]    // 靜音比較近
    [InlineData(5.02, 5.0, 0.2, 5.0, SnapKind.Playhead)]   // 播放頭比較近
    [InlineData(4.95, 5.0, 0.2, 5.0, SnapKind.Playhead)]   // 一樣近：播放頭優先
    [InlineData(7.0, 9.0, 0.2, 7.0, SnapKind.None)]        // 都太遠
    public void NearestSnapWins(double time, double playhead, double tolerance, double expected, SnapKind kind)
    {
        var result = TimelineSnap.Snap(time, playhead, [3.0, 4.9, 12.0], tolerance);
        Assert.Equal(expected, result.Time, 9);
        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void NoTargetsOrNoToleranceDoesNotSnap()
    {
        Assert.Equal(SnapKind.None, TimelineSnap.Snap(3.0, null, [], 0.2).Kind);
        Assert.Equal(SnapKind.None, TimelineSnap.Snap(3.0, 3.0, [3.0], 0).Kind);
        Assert.Equal(SnapKind.Silence, TimelineSnap.Snap(3.1, null, [3.0], 0.2).Kind);
    }

    [Fact]
    public void TrimAlignedToFrameStaysInsideTheSilence()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 10);
        var target = TimelineSnap.Snap(4.93, null, TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.End), 0.2);
        Assert.Equal(SnapKind.Silence, target.Kind);
        t.TrimEnd(clip, target.Time);
        Assert.True(TimelineSnap.IsAt(clip.End, target.Target, 30));
        Assert.InRange(clip.SourceOut, 5, 8);
        Assert.Equal(0, Math.Round(clip.SourceOut * 30 % 1, 6) % 1);

        var start = TimelineSnap.Snap(1.85, null, TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.Start), 0.2);
        t.TrimStart(clip, start.Time);
        Assert.True(TimelineSnap.IsAt(clip.Start, start.Target, 30));
        Assert.InRange(clip.SourceIn, 1, 2);
    }

    [Fact]
    public void BlockedByNeighbourIsNotSnapped()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 4);
        var next = t.Append(Media(), 0, 1);
        t.Move(next, 4.5);
        var target = TimelineSnap.Snap(4.9, null, TimelineSnap.SilenceTargets(clip, Silences, TrimEdge.End), 0.2);
        Assert.Equal(SnapKind.Silence, target.Kind);
        t.TrimEnd(clip, target.Time);
        Assert.Equal(4.5, clip.End, 6);
        Assert.False(TimelineSnap.IsAt(clip.End, target.Target, 30));
    }
}

[Collection("media")]
public class SilenceSnapIntegrationTests(TestMedia media)
{
    [Fact]
    public async Task TargetsComeFromTheRealWaveform()
    {
        var path = media.Output("silence-step.wav");
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
        [
            "-hide_banner", "-y", "-v", "error", "-f", "lavfi",
            "-i", "aevalsrc='if(between(t,1,2),0.5*sin(2*PI*440*t),0)':s=48000:d=3", "-c:a", "pcm_s16le", path,
        ]);
        var info = await media.Probe(path);
        var data = await new WaveformExtractor(media.Tools).ExtractAsync(info);
        var silences = SilenceDetector.Find(data);
        Assert.Equal(2, silences.Count);

        var t = new TimelineModel();
        var clip = t.Append(info, 0, info.Duration);
        var start = TimelineSnap.SilenceTargets(clip, silences, TrimEdge.Start);
        var end = TimelineSnap.SilenceTargets(clip, silences, TrimEdge.End);
        Assert.InRange(start[0], 0.89, 0.91);
        Assert.InRange(end[1], 2.09, 2.11);
    }
}
