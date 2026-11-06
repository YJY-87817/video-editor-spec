using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>驗證匯入流程對畫面的通知順序與結果；實際呼叫 FFmpeg。</summary>
[Collection("media")]
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

    private sealed class FakeView : IImportView
    {
        public HashSet<string> Known { get; } = [];
        public List<(MediaInfo Info, byte[]? Thumbnail)> Added { get; } = [];
        public List<(string Text, double Value)> Progress { get; } = [];
        public List<(string Name, string Message)> Failures { get; } = [];
        public List<string> Events { get; } = [];
        public int Hidden { get; private set; }

        public bool IsAlreadyImported(string path) => Known.Contains(path);

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

        public void AddMedia(MediaInfo info, byte[]? thumbnail) => Added.Add((info, thumbnail));
        public void ShowFailure(string name, string message) => Failures.Add((name, message));
    }
}
