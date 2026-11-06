using VideoEditor.Core.Ffmpeg;

namespace VideoEditor.Tests;

/// <summary>把 FFmpeg 輸出切成固定大小區塊（rawvideo 影格）的讀取方式。</summary>
[Collection("media")]
public class ProcessRunnerTests(TestMedia media)
{
    // 10 格 4×2 灰階影格，每格 8 個位元組。
    private static readonly string[] Gray =
        ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=gray:s=4x2:r=10:d=1", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"];

    [Fact]
    public async Task ChunksMatchTheWholeOutput()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 8, 100);

        Assert.True(whole.Length >= 16);
        Assert.Equal(whole.Length / 8, chunks.Count);
        Assert.Equal(whole, chunks.SelectMany(c => c).ToArray());
        Assert.NotSame(chunks[0], chunks[1]); // 每塊是各自的陣列，不會被下一塊覆寫
    }

    [Fact]
    public async Task StopsAtTheLimitAndStillFinishes()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 8, 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(whole.Take(24), chunks.SelectMany(c => c));
    }

    [Fact]
    public async Task IncompleteTailIsDropped()
    {
        var whole = await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg, Gray);
        var chunks = await ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, Gray, 24, 100);

        Assert.Equal(whole.Length / 24, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(24, c.Length));
    }

    [Fact]
    public async Task FailureStillReportsStderr()
    {
        var error = await Assert.ThrowsAsync<FfmpegException>(() =>
            ProcessRunner.RunForChunksAsync(media.Tools.Ffmpeg, ["-v", "error", "-i", media.Output("missing.mp4"), "-f", "rawvideo", "pipe:1"], 8, 1));
        Assert.False(string.IsNullOrWhiteSpace(error.Stderr));
    }
}
