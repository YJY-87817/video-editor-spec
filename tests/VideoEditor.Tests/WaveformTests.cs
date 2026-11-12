using System.Buffers.Binary;
using System.Globalization;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.Tests;

public class WaveformDataTests
{
    private static WaveformData Random(int count, int seed, out float[] peaks)
    {
        var random = new Random(seed);
        peaks = Enumerable.Range(0, count).Select(_ => (float)random.NextDouble()).ToArray();
        return new WaveformData(peaks);
    }

    [Fact]
    public void PeakMatchesBruteForceForAnyRange()
    {
        // 長度刻意取奇數，涵蓋各層最後一格沒有配對的情況。
        var data = Random(1001, 7, out var peaks);
        var random = new Random(3);
        for (var i = 0; i < 2000; i++)
        {
            var a = random.Next(0, 1001);
            var b = random.Next(a + 1, 1002);
            var expected = peaks[a..b].Max();
            Assert.Equal(expected, data.Peak(a * WaveformData.SecondsPerPeak, b * WaveformData.SecondsPerPeak));
        }
    }

    [Fact]
    public void RangeShorterThanOneBucketUsesThatBucket()
    {
        var data = new WaveformData([0.1f, 0.9f, 0.2f]);
        var middle = 1.3 * WaveformData.SecondsPerPeak;
        Assert.Equal(0.9f, data.Peak(middle, middle + WaveformData.SecondsPerPeak * 0.2));
        Assert.Equal(0.9f, data.Peak(middle, middle));
    }

    [Fact]
    public void OutsideTheMediaIsSilent()
    {
        var data = new WaveformData([0.5f, 0.5f]);
        Assert.Equal(0, data.Peak(1, 2));
        Assert.Equal(0, data.Peak(-1, -0.5));
        Assert.Equal(0.5f, data.Peak(-1, 1));
    }

    [Fact]
    public void ReversedArgumentsAreAccepted()
    {
        var data = new WaveformData([0.1f, 0.2f, 0.3f, 0.4f]);
        Assert.Equal(data.Peak(0, 0.006), data.Peak(0.006, 0));
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(0.5, 2.0, 1.0)]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.001, 1.0, 0.0)]       // -60 dB，低於下限
    [InlineData(0.0039810717, 1.0, 0)] // -48 dB
    [InlineData(0.0630957344, 1.0, 0.5)] // -24 dB
    [InlineData(0.5, 0, 0)]
    public void LevelIsDecibelScale(double peak, double volume, double expected)
    {
        Assert.Equal(expected, WaveformData.Level(peak, volume), 6);
    }

    [Fact]
    public void ClippingOnlyAboveFullScale()
    {
        Assert.False(WaveformData.Clips(1.0, 1.0));
        Assert.False(WaveformData.Clips(0.6, 1.5));
        Assert.True(WaveformData.Clips(0.6, 2.0));
    }
}

public class WaveformBuilderTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    [Fact]
    public void BucketsFollowSampleRate()
    {
        // 1000 Hz：每格 2 個取樣點。
        var builder = new WaveformBuilder(1000, 1);
        builder.Add(Pcm(100, -16384, 0, 0, 32767, 5));
        var data = builder.Build();
        Assert.Equal(3, data.Count);
        Assert.Equal(0.5f, data.Peak(0, WaveformData.SecondsPerPeak));
        Assert.Equal(0f, data.Peak(WaveformData.SecondsPerPeak, 2 * WaveformData.SecondsPerPeak));
        Assert.Equal(32767 / 32768f, data.Peak(2 * WaveformData.SecondsPerPeak, 3 * WaveformData.SecondsPerPeak));
    }

    [Fact]
    public void ChannelsTakeTheLouderSide()
    {
        // 左右反相：混成單聲道會抵消，這裡要取各聲道絕對值的最大值。
        var builder = new WaveformBuilder(500, 2);
        builder.Add(Pcm(16384, -16384, -8192, 8192));
        var data = builder.Build();
        Assert.Equal(2, data.Count);
        Assert.Equal(0.5f, data.Peak(0, WaveformData.SecondsPerPeak));
        Assert.Equal(0.25f, data.Peak(WaveformData.SecondsPerPeak, 2 * WaveformData.SecondsPerPeak));
    }

    [Fact]
    public void ChunksMaySplitASample()
    {
        var bytes = Pcm(1000, -2000, 3000, -32768, 4000, 5000, -6000, 7000);
        var whole = new WaveformBuilder(1000, 2);
        whole.Add(bytes);
        var split = new WaveformBuilder(1000, 2);
        foreach (var size in new[] { 1, 3, 2, 5, 1, 4 })
        {
            var take = Math.Min(size, bytes.Length);
            split.Add(bytes.AsSpan(0, take));
            bytes = bytes[take..];
        }
        split.Add(bytes);
        var a = whole.Build();
        var b = split.Build();
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a.Peak(i * WaveformData.SecondsPerPeak, (i + 1) * WaveformData.SecondsPerPeak),
                b.Peak(i * WaveformData.SecondsPerPeak, (i + 1) * WaveformData.SecondsPerPeak));
        Assert.Equal(1f, a.Peak(0, 1));
    }

    [Fact]
    public void NoSamplesGivesEmptyWaveform()
    {
        var data = new WaveformBuilder(48000, 2).Build();
        Assert.Equal(0, data.Count);
        Assert.Equal(0, data.Peak(0, 1));
    }
}

public class WaveformColumnsTests
{
    private static MediaInfo Media() => new()
    {
        Path = "a.mp4", Duration = 10, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    /// <summary>0–1 秒靜音、1–2 秒滿格、之後一半。</summary>
    private static WaveformData Steps()
    {
        var peaks = new float[10 * WaveformData.PeaksPerSecond];
        for (var i = 0; i < peaks.Length; i++)
            peaks[i] = i < WaveformData.PeaksPerSecond ? 0 : i < 2 * WaveformData.PeaksPerSecond ? 1 : 0.5f;
        return new WaveformData(peaks);
    }

    [Fact]
    public void ColumnsFollowSourceRangeOfTheClip()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0.5, 2.5);
        // 每秒 10 像素，片段在畫面 x=100 開始，長 2 秒 = 20 欄。
        var columns = WaveformColumns.Build(clip, Steps(), 100, 10, 0, 1000);
        Assert.Equal(20, columns.Count);
        Assert.Equal(100, columns[0].X);
        Assert.All(columns.Take(5), c => Assert.Equal(0, c.Level));
        Assert.All(columns.Skip(5).Take(10), c => Assert.Equal(1, c.Level));
        Assert.All(columns.Skip(15), c => Assert.Equal(WaveformData.Level(0.5, 1), c.Level));
    }

    [Fact]
    public void ReversedClipIsMirrored()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0.5, 2.5);
        var forward = WaveformColumns.Build(clip, Steps(), 0, 10, 0, 1000).Select(c => c.Level).ToList();
        t.SetReversed(clip, true);
        var reversed = WaveformColumns.Build(clip, Steps(), 0, 10, 0, 1000).Select(c => c.Level).ToList();
        forward.Reverse();
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void OnlyVisiblePartIsBuilt()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 10);
        // 片段在畫面上從 -50 到 150，只有 44–80 看得到。
        var columns = WaveformColumns.Build(clip, Steps(), -50, 20, 44, 80);
        Assert.Equal(36, columns.Count);
        Assert.Equal(44, columns[0].X);
        Assert.True(columns[^1].X + columns[^1].Width <= 80 + 1e-9);
    }

    [Fact]
    public void VolumeScalesAndMarksClipping()
    {
        var t = new TimelineModel();
        var clip = t.Append(Media(), 0, 10);
        clip.Volume = 1.5;
        var columns = WaveformColumns.Build(clip, Steps(), 0, 10, 0, 1000);
        Assert.All(columns.Take(10), c => Assert.False(c.Clipped));
        Assert.All(columns.Skip(10).Take(10), c => Assert.True(c.Clipped));
        Assert.All(columns.Skip(20), c => Assert.False(c.Clipped));
        Assert.Equal(WaveformData.Level(0.75, 1), columns[30].Level, 9);
    }
}

public class WaveformCacheTests
{
    private static MediaInfo Media(string path, bool audio = true) => new() { Path = path, Duration = 1, HasAudio = audio };

    [Fact]
    public async Task SameMediaIsExtractedOnce()
    {
        var calls = 0;
        using var cache = new WaveformCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new WaveformData([0.5f]));
        });
        var ready = 0;
        cache.Ready += _ => Interlocked.Increment(ref ready);
        var first = cache.Request(Media("A.mp4"));
        var second = cache.Request(Media("a.mp4"));
        Assert.Same(first, second);
        Assert.NotNull(await first);
        Assert.Equal(1, calls);
        Assert.Equal(1, ready);
        Assert.NotNull(cache.Get(Media("a.mp4")));
        Assert.False(cache.IsPending(Media("a.mp4")));
    }

    [Fact]
    public async Task MediaWithoutAudioIsSkipped()
    {
        var calls = 0;
        using var cache = new WaveformCache((_, _) => { calls++; return Task.FromResult(new WaveformData([1f])); });
        Assert.Null(await cache.Request(Media("v.mp4", audio: false)));
        Assert.Equal(0, calls);
        Assert.False(cache.IsPending(Media("v.mp4", audio: false)));
    }

    [Fact]
    public async Task FailureIsReportedNotThrown()
    {
        using var cache = new WaveformCache((_, _) => throw new FfmpegException("boom", "bad data"));
        Exception? reported = null;
        cache.Failed += (_, e) => reported = e;
        Assert.Null(await cache.Request(Media("bad.mp4")));
        Assert.IsType<FfmpegException>(reported);
        Assert.Null(cache.Get(Media("bad.mp4")));
    }

    [Fact]
    public async Task OnlyOneExtractionRunsAtATime()
    {
        var running = 0;
        var maxRunning = 0;
        using var cache = new WaveformCache(async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return new WaveformData([0.1f]);
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => cache.Request(Media($"{i}.mp4"))));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task DisposeCancelsRunningWork()
    {
        var started = new TaskCompletionSource();
        var cache = new WaveformCache(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new WaveformData([1f]);
        });
        var failed = false;
        cache.Failed += (_, _) => failed = true;
        var task = cache.Request(Media("long.mp4"));
        await started.Task;
        cache.Dispose();
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(failed);
        Assert.Null(await cache.Request(Media("later.mp4")));
    }
}

[Collection("media")]
public class WaveformExtractorTests(TestMedia media)
{
    private async Task<string> Wav(string name, string source, params string[] options)
    {
        var path = media.Output(name);
        var args = new List<string> { "-hide_banner", "-y", "-v", "error", "-f", "lavfi", "-i", source };
        args.AddRange(options);
        args.AddRange(["-c:a", "pcm_s16le", path]);
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, args);
        return path;
    }

    private Task<WaveformData> Extract(MediaInfo info, CancellationToken token = default) =>
        new WaveformExtractor(media.Tools).ExtractAsync(info, token);

    [Fact]
    public async Task SilenceThenToneSwitchesAtOneSecond()
    {
        var path = await Wav("wave-step.wav", "aevalsrc='if(lt(t,1),0,0.5*sin(2*PI*440*t))':s=48000:d=2");
        var data = await Extract(await media.Probe(path));
        Assert.InRange(data.Duration, 1.99, 2.01);
        Assert.Equal(0, data.Peak(0, 0.99));
        Assert.InRange(data.Peak(1.01, 2), 0.49, 0.51);
        Assert.InRange(data.Peak(1.5, 1.52), 0.45, 0.51);
    }

    [Fact]
    public async Task OppositePhaseChannelsAreNotCancelled()
    {
        var path = await Wav("wave-phase.wav", "aevalsrc='0.5*sin(2*PI*300*t)|-0.5*sin(2*PI*300*t)':s=44100:d=1");
        var info = await media.Probe(path);
        Assert.Equal(2, info.AudioChannels);
        var data = await Extract(info);
        Assert.InRange(data.Duration, 0.99, 1.01);
        Assert.InRange(data.Peak(0.2, 0.8), 0.49, 0.51);
    }

    [Fact]
    public async Task SurroundIsMixedToStereo()
    {
        var path = await Wav("wave-51.wav", "aevalsrc='0.3*sin(2*PI*300*t)|0|0|0|0|0':s=48000:d=1:c=5.1");
        var info = await media.Probe(path);
        Assert.Equal(6, info.AudioChannels);
        var data = await Extract(info);
        Assert.InRange(data.Duration, 0.99, 1.01);
        Assert.True(data.Peak(0, 1) > 0.1);
    }

    [Fact]
    public async Task VideoWithAacAudioCoversItsLength()
    {
        var info = await media.Probe(media.Landscape);
        var data = await Extract(info);
        Assert.InRange(data.Duration, info.Duration - 0.1, info.Duration + 0.1);
        // lavfi 的 sine 預設振幅 1/8。
        Assert.InRange(data.Peak(1, 3), 0.1, 0.15);
    }

    [Fact]
    public async Task CancelledExtractionThrows()
    {
        var info = await media.Probe(media.Landscape);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extract(info, cancel.Token));
    }

    [Fact]
    public async Task MediaWithoutAudioIsRejected()
    {
        var info = await media.Probe(media.NoAudio);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Extract(info));
    }
}
