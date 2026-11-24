using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>驗證匯入流程對畫面的通知順序與結果；實際呼叫 FFmpeg。</summary>
[Collection("media")]
[Trait("Category", "Integration")]
public class ImportControllerTests(TestMedia media)
{
    private ImportController Create(FakeView view) =>
        new(new MediaProbe(media.Tools), new FrameExtractor(media.Tools), view);

    [Fact]
    public async Task VideoGetsAThumbnailAndAudioDoesNot()
    {
        var view = new FakeView();

        await Create(view).ImportAsync([media.Landscape, media.Music]);

        Assert.Equal(2, view.Added.Count);
        var video = view.Added.Single(a => a.Info.HasVideo);
        Assert.NotNull(video.Thumbnail);
        Assert.Equal(0xFF, video.Thumbnail![0]); // JPEG
        Assert.Null(view.Added.Single(a => !a.Info.HasVideo).Thumbnail);
        Assert.Empty(view.Failures);
    }

    [Fact]
    public async Task CancelStopsTheRestWithoutReportingAFailure()
    {
        var view = new FakeView();
        var controller = Create(view);
        view.OnAdded = controller.Cancel; // 第一個檔案加入後取消

        await controller.ImportAsync([media.Landscape, media.Music, media.Portrait]);

        var added = Assert.Single(view.Added);
        Assert.Equal(media.Landscape, added.Info.Path);
        Assert.Empty(view.Failures); // 取消不是失敗
        Assert.Equal("Hide", view.Events[^1]); // 進度要收起來
    }

    [Fact]
    public async Task FilesSkippedByCancelCanBeImportedAgain()
    {
        var view = new FakeView();
        var controller = Create(view);
        view.OnAdded = controller.Cancel;
        await controller.ImportAsync([media.Landscape, media.Music]);
        view.OnAdded = null;

        await controller.ImportAsync([media.Landscape, media.Music]);

        Assert.Equal([media.Landscape, media.Music], view.Added.Select(a => a.Info.Path));
        Assert.Empty(view.Failures);
    }

    [Fact]
    public async Task AlreadyImportedFilesAreSkipped()
    {
        var view = new FakeView { Known = { media.Landscape } };

        await Create(view).ImportAsync([media.Landscape]);

        Assert.Empty(view.Added);
        Assert.Empty(view.Progress); // 沒有要匯入的檔案時不顯示進度
        Assert.Equal(0, view.Hidden);
    }

    [Fact]
    public async Task ProgressCoversAllFilesAndHidesAtTheEnd()
    {
        var view = new FakeView();

        await Create(view).ImportAsync([media.Landscape, media.Music]);

        Assert.Equal("讀取素材 1/2：landscape.mp4（讀取資訊）", view.Progress[0].Text);
        Assert.Equal(0, view.Progress[0].Value);
        Assert.Contains(view.Progress, p => p.Text == "讀取素材 1/2：landscape.mp4（產生縮圖）" && p.Value == 0.25);
        Assert.Contains(view.Progress, p => p.Text == "讀取素材 2/2：music.m4a（讀取資訊）" && p.Value == 0.5);
        Assert.Equal(view.Progress.Select(p => p.Value).Order(), view.Progress.Select(p => p.Value));
        Assert.Equal("Hide", view.Events[^1]);
        Assert.Equal(1, view.Hidden);
    }

    [Fact]
    public async Task UnreadableFileIsReportedAndTheRestStillImport()
    {
        var bad = media.Output("not-a-video.mp4");
        await File.WriteAllTextAsync(bad, "這不是影片");
        var view = new FakeView();

        await Create(view).ImportAsync([bad, media.Landscape]);

        var failure = Assert.Single(view.Failures);
        Assert.Equal("not-a-video.mp4", failure.Name);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.Single(view.Added);
        Assert.Equal(media.Landscape, view.Added[0].Info.Path);
        Assert.Equal("Hide", view.Events[^1]);
    }

    [Theory]
    [InlineData("still.png")]
    [InlineData("still.jpg")] // JPEG 加 -ss 會解不出畫面，縮圖要不加 -ss
    public async Task StillImageIsImportedWithAThumbnail(string name)
    {
        var still = media.Output(name);
        await ProcessRunner.RunForBytesAsync(media.Tools.Ffmpeg,
            ["-hide_banner", "-y", "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", still]);
        var view = new FakeView();

        await Create(view).ImportAsync([still, media.Landscape]);

        Assert.Empty(view.Failures);
        Assert.Equal(2, view.Added.Count);
        var added = view.Added.Single(a => a.Info.Path == still);
        Assert.True(added.Info.IsStill);
        Assert.NotNull(added.Thumbnail);
        Assert.Equal(0xFF, added.Thumbnail![0]); // JPEG
    }

    [Fact]
    public async Task SameFileDroppedAgainWhileImportingIsNotDuplicated()
    {
        var view = new FakeView();
        var controller = Create(view);

        // 第一次還在讀取（尚未加入素材清單）時，又拖進同一個檔案。
        var first = controller.ImportAsync([media.Landscape]);
        var second = controller.ImportAsync([media.Landscape]);
        await Task.WhenAll(first, second);

        Assert.Single(view.Added);
    }

    [Fact]
    public async Task SameFileTwiceInOneDropIsImportedOnce()
    {
        var view = new FakeView();

        await Create(view).ImportAsync([media.Landscape, media.Landscape]);

        Assert.Single(view.Added);
        Assert.Equal("讀取素材 1/1：landscape.mp4（讀取資訊）", view.Progress[0].Text);
    }

    [Fact]
    public async Task FailedFileCanBeTriedAgain()
    {
        var bad = media.Output("retry-not-a-video.mp4");
        await File.WriteAllTextAsync(bad, "這不是影片");
        var view = new FakeView();
        var controller = Create(view);

        await controller.ImportAsync([bad]);
        await controller.ImportAsync([bad]);

        Assert.Equal(2, view.Failures.Count);
    }

    [Fact]
    public async Task ToolThatCannotStartIsReportedAndFilesCanBeRetried()
    {
        // 模擬 ffprobe 被防毒軟體擋下或被刪掉：程式無法啟動。
        var missing = Path.Combine(media.Directory, "no-such-tools");
        var tools = new FfmpegTools(Path.Combine(missing, "ffmpeg.exe"), Path.Combine(missing, "ffprobe.exe"));
        var view = new FakeView();
        var controller = new ImportController(new MediaProbe(tools), new FrameExtractor(tools), view);

        await controller.ImportAsync([media.Landscape, media.Music]);
        Assert.Equal("landscape.mp4,music.m4a", string.Join(",", view.Failures.Select(f => f.Name)));
        Assert.Equal("Hide", view.Events[^1]);

        // 兩個檔案都沒有卡在「排入中」，可以再試。
        await controller.ImportAsync([media.Landscape, media.Music]);
        Assert.Equal(4, view.Failures.Count);
    }

    [Fact]
    public async Task UnexpectedErrorDoesNotLeaveTheRestOfTheDropStuck()
    {
        var view = new FakeView { ThrowOnAdd = true };
        var controller = Create(view);

        // 第一個檔案加入清單時發生預料之外的錯誤，迴圈中斷，第二個檔案還沒處理。
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ImportAsync([media.Landscape, media.Music]));
        Assert.Equal("Hide", view.Events[^1]); // 進度沒有卡在畫面上

        view.ThrowOnAdd = false;
        view.Progress.Clear();
        await controller.ImportAsync([media.Music]);

        Assert.Single(view.Added); // 沒處理到的檔案之後仍可匯入
        Assert.Equal("讀取素材 1/1：music.m4a（讀取資訊）", view.Progress[0].Text); // 進度計數也已歸零
    }

    private sealed class FakeView : IImportView
    {
        public HashSet<string> Known { get; } = [];
        public List<(MediaInfo Info, byte[]? Thumbnail)> Added { get; } = [];
        public List<(string Text, double Value)> Progress { get; } = [];
        public List<(string Name, string Message)> Failures { get; } = [];
        public List<string> Events { get; } = [];
        public int Hidden { get; private set; }
        public bool ThrowOnAdd { get; set; }
        public Action? OnAdded { get; set; }

        // 與視窗相同：素材清單裡已有的才算已匯入。
        public bool IsAlreadyImported(string path) => Known.Contains(path) || Added.Any(a => a.Info.Path == path);

        public void ShowProgress(string text, double value)
        {
            Progress.Add((text, value));
            Events.Add("Progress");
        }

        public void HideProgress()
        {
            Hidden++;
            Events.Add("Hide");
        }

        public void AddMedia(MediaInfo info, byte[]? thumbnail)
        {
            if (ThrowOnAdd) throw new InvalidOperationException("模擬預料之外的錯誤");
            Added.Add((info, thumbnail));
            OnAdded?.Invoke();
        }
        public void ShowFailure(string name, string message) => Failures.Add((name, message));
    }
}
