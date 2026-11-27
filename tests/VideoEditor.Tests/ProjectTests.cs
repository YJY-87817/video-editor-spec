using VideoEditor.Core.Editing;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Project;

namespace VideoEditor.Tests;

/// <summary>專案檔（UIUX-PLAN-2 階段 6）：存讀一致、找素材、內容檢查、重建時間軸、有沒有沒存的變更、自動存檔的時機。</summary>
public sealed class ProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoEditorProjectTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string In(params string[] parts) => Path.Combine([_root, .. parts]);

    private static MediaInfo Video(string path, double duration = 20) => new()
    {
        Path = path, Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    private static MediaInfo Music(string path) => new() { Path = path, Duration = 60, HasAudio = true };

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>兩段影片（第二段倒放、旋轉、調畫面位置、空白後）與一段配樂（調過音量）。</summary>
    private (Timeline Timeline, MediaInfo A, MediaInfo B, MediaInfo Song, MediaInfo Unused) Sample()
    {
        var a = Video(In("clips", "a.mp4"));
        var b = Video(In("clips", "b.mp4"));
        var song = Music(In("clips", "song.m4a"));
        var unused = Video(In("clips", "unused.mp4"));
        var timeline = new Timeline();
        timeline.Append(a, 1, 4);
        var second = timeline.Append(b, 2, 5);
        timeline.Move(second, 6);
        timeline.SetReversed(second, true);
        timeline.SetTransform(second, second.Transform.Rotate(90).ToggleHorizontal());
        timeline.SetFraming(second, new ClipFraming(1.5, 0.4, 0.6));
        var music = timeline.Append(song, 0, 8);
        music.Volume = 0.5;
        return (timeline, a, b, song, unused);
    }

    private static IEnumerable<object> Describe(Timeline timeline) => timeline.AllClips.Select(c =>
        (object)(c.Media.Path, c.Track, c.SourceIn, c.SourceOut, c.Start, c.Volume, c.Reversed, c.Transform, c.Framing));

    [Fact]
    public void SavedProjectLoadsBackTheSameTimelineAndSettings()
    {
        var (timeline, a, b, song, unused) = Sample();
        var path = In("project", "trip.vedit");
        ProjectSerializer.Save(ProjectSerializer.Create(timeline, [unused, a], AspectRatio.R9x16, FitMode.Pad, path), path);

        var loaded = ProjectSerializer.Load(path);

        Assert.Null(loaded.Error);
        var project = loaded.File!;
        Assert.Equal(AspectRatio.R9x16, project.Ratio);
        Assert.Equal(FitMode.Pad, project.Mode);
        // 素材清單的順序照原本的清單，時間軸用到但不在清單裡的接在後面。
        Assert.Equal([unused.Path, a.Path, b.Path, song.Path], project.Media.Select(m => m.Path));

        var byPath = new Dictionary<string, MediaInfo> { [a.Path] = a, [b.Path] = b, [song.Path] = song, [unused.Path] = unused };
        var rebuilt = new Timeline();
        var problems = ProjectSerializer.Build(project, [.. project.Media.Select(m => (MediaInfo?)byPath[m.Path])], rebuilt);

        Assert.Empty(problems);
        Assert.Equal(Describe(timeline), Describe(rebuilt));
    }

    [Fact]
    public void RelativePathFindsMediaAfterTheFolderMoves()
    {
        var (timeline, _, _, _, _) = Sample();
        var path = In("project", "trip.vedit");
        var project = ProjectSerializer.Create(timeline, [], AspectRatio.Original, FitMode.Fill, path);
        Assert.Equal(Path.Combine("..", "clips", "a.mp4"), project.Media[0].RelativePath);

        // 整個資料夾搬到 moved 底下：絕對路徑找不到，用相對路徑找到。
        var movedProject = In("moved", "project", "trip.vedit");
        var movedClip = In("moved", "clips", "a.mp4");
        bool Exists(string p) => p == movedClip;

        Assert.Equal(movedClip, ProjectSerializer.ResolveMediaPath(project.Media[0], movedProject, Exists));
        Assert.Null(ProjectSerializer.ResolveMediaPath(project.Media[1], movedProject, Exists));
    }

    [Fact]
    public void AbsolutePathIsPreferred()
    {
        var media = new ProjectMedia(In("clips", "a.mp4"), Path.Combine("..", "clips", "a.mp4"));
        Assert.Equal(media.Path, ProjectSerializer.ResolveMediaPath(media, In("elsewhere", "p.vedit"), _ => true));
    }

    [Theory]
    [InlineData("這不是 JSON", "格式不對")]
    [InlineData("null", "空的")]
    [InlineData("{\"Version\": 9}", "較新版本")]
    [InlineData("{\"Version\": 1, \"Media\": [], \"Video\": [{\"Media\": 0, \"SourceIn\": 0, \"SourceOut\": 1, \"Start\": 0}]}", "不存在的素材")]
    [InlineData("{\"Media\": [{\"Path\": \"a.mp4\"}], \"Video\": [{\"Media\": 0, \"SourceIn\": 3, \"SourceOut\": 1, \"Start\": 0}]}", "時間不對")]
    [InlineData("{\"Media\": [{\"Path\": \"a.mp4\"}], \"Video\": [{\"Media\": 0, \"SourceIn\": 0, \"SourceOut\": 1, \"Start\": 0, \"Volume\": 9}]}", "音量")]
    [InlineData("{\"Media\": [{\"Path\": \"a.mp4\"}], \"Video\": [{\"Media\": 0, \"SourceIn\": 0, \"SourceOut\": 1, \"Start\": 0, \"Rotation\": 45}]}", "旋轉")]
    [InlineData("{\"Ratio\": {\"W\": 7, \"H\": 3}}", "畫面比例")]
    [InlineData("{\"Video\": null}", "缺少")]
    public void BrokenProjectExplainsWhyWithoutThrowing(string content, string reason)
    {
        Directory.CreateDirectory(_root);
        var path = In("broken.vedit");
        File.WriteAllText(path, content);

        var result = ProjectSerializer.Load(path);

        Assert.Null(result.File);
        Assert.Contains(reason, result.Error);
    }

    [Fact]
    public void MissingFileExplainsWhy() =>
        Assert.Contains("讀不到", ProjectSerializer.Load(In("nope.vedit")).Error);

    [Fact]
    public void BuildSkipsClipsWhoseMediaIsMissingOrTooShort()
    {
        var (timeline, a, b, song, _) = Sample();
        var project = ProjectSerializer.Create(timeline, [], AspectRatio.Original, FitMode.Fill, In("p.vedit"));
        var shortB = b with { Duration = 3 }; // 檔案被換成比較短的：片段用到 2–5 秒

        var rebuilt = new Timeline();
        var problems = ProjectSerializer.Build(project, [a, shortB, null], rebuilt);

        Assert.Single(rebuilt.VideoClips);
        Assert.Empty(rebuilt.MusicClips);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("b.mp4") && p.Contains("短"));
        Assert.Contains(problems, p => p.Contains("song.m4a") && p.Contains("找不到"));
    }

    [Fact]
    public void BuildSkipsOverlappingClips()
    {
        var media = Video(In("a.mp4"));
        var project = new ProjectFile
        {
            Media = [new ProjectMedia(media.Path, null)],
            Video = [new ProjectClip(0, 0, 4, 0), new ProjectClip(0, 0, 2, 3)],
        };

        var rebuilt = new Timeline();
        var problems = ProjectSerializer.Build(project, [media], rebuilt);

        Assert.Single(rebuilt.VideoClips);
        Assert.Contains("重疊", Assert.Single(problems));
    }

    [Fact]
    public void BuildReplacesWhateverWasOnTheTimeline()
    {
        var timeline = new Timeline();
        timeline.Append(Video(In("old.mp4")), 0, 5);
        ProjectSerializer.Build(new ProjectFile(), [], timeline);
        Assert.Empty(timeline.AllClips);
    }

    private static ProjectState State(Timeline timeline, AspectRatio? ratio = null, params string[] media) =>
        new(timeline.Capture(), ratio ?? AspectRatio.Original, FitMode.Fill, media);

    [Fact]
    public void StateDetectsUnsavedChanges()
    {
        var (timeline, _, _, _, _) = Sample();
        var history = new TimelineHistory(timeline);
        var saved = State(timeline, null, "a.mp4");

        Assert.Equal(saved, State(timeline, null, "a.mp4"));

        var before = history.Capture();
        timeline.MusicClips[0].Volume = 1;
        history.Commit(before, "音量");
        Assert.NotEqual(saved, State(timeline, null, "a.mp4"));

        history.Undo(); // 復原回存檔時的樣子：沒有變更
        Assert.Equal(saved, State(timeline, null, "a.mp4"));

        Assert.NotEqual(saved, State(timeline, AspectRatio.R1x1, "a.mp4")); // 改輸出設定
        Assert.NotEqual(saved, State(timeline, null, "a.mp4", "b.mp4"));    // 匯入素材
    }

    [Fact]
    public void AutoSaveWaitsForThreeQuietSecondsAndSavesOnlyOnce()
    {
        var time = new ManualTime();
        var tracker = new AutoSaveTracker(time);
        var timeline = new Timeline();
        tracker.Reset(State(timeline));
        Assert.False(tracker.Observe(State(timeline))); // 沒有修改

        timeline.Append(Video(In("a.mp4")), 0, 2);
        Assert.False(tracker.Observe(State(timeline))); // 剛改
        time.Now += TimeSpan.FromSeconds(2);
        Assert.False(tracker.Observe(State(timeline)));
        time.Now += TimeSpan.FromSeconds(1);
        var state = State(timeline);
        Assert.True(tracker.Observe(state));            // 3 秒沒有再改
        tracker.MarkSaved(state);

        time.Now += TimeSpan.FromSeconds(10);
        Assert.False(tracker.Observe(State(timeline))); // 已經存過
    }

    [Fact]
    public void AutoSaveRestartsTheWaitOnEveryChange()
    {
        var time = new ManualTime();
        var tracker = new AutoSaveTracker(time);
        var timeline = new Timeline();
        tracker.Reset(State(timeline));
        var clip = timeline.Append(Video(In("a.mp4")), 0, 2);
        tracker.Observe(State(timeline));

        time.Now += TimeSpan.FromSeconds(2);
        clip.Volume = 0.5; // 還在修改
        Assert.False(tracker.Observe(State(timeline)));
        time.Now += TimeSpan.FromSeconds(2);
        Assert.False(tracker.Observe(State(timeline)));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.True(tracker.Observe(State(timeline)));
    }

    [Fact]
    public void HistoryClearRemovesUndoAndRedo()
    {
        var (timeline, _, _, _, _) = Sample();
        var history = new TimelineHistory(timeline);
        var before = history.Capture();
        timeline.MusicClips[0].Volume = 1;
        history.Commit(before, "音量");
        history.Undo();
        Assert.True(history.CanRedo);

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}

/// <summary>用 FFmpeg 產生的真實影片：存檔後重新讀取素材資訊並重建時間軸。</summary>
[Collection("media")]
[Trait("Category", "Integration")]
public class ProjectIntegrationTests(TestMedia media)
{
    [Fact]
    public async Task ProjectWithRealMediaOpensAgain()
    {
        var probe = new MediaProbe(media.Tools);
        var landscape = await probe.ProbeAsync(media.Landscape);
        var music = await probe.ProbeAsync(media.Music);
        var timeline = new Timeline();
        timeline.Append(landscape, 0.5, 2.5);
        timeline.Append(music, 0, 1);
        var path = media.Output("project.vedit");
        ProjectSerializer.Save(ProjectSerializer.Create(timeline, [landscape, music], AspectRatio.R1x1, FitMode.Fill, path), path);

        var project = ProjectSerializer.Load(path).File!;
        var infos = new List<MediaInfo?>();
        foreach (var item in project.Media)
            infos.Add(ProjectSerializer.ResolveMediaPath(item, path, File.Exists) is { } found ? await probe.ProbeAsync(found) : null);
        var rebuilt = new Timeline();
        var problems = ProjectSerializer.Build(project, infos, rebuilt);

        Assert.Empty(problems);
        Assert.Equal(timeline.Capture().Video.Select(c => (c.SourceIn, c.SourceOut, c.Start)), rebuilt.Capture().Video.Select(c => (c.SourceIn, c.SourceOut, c.Start)));
        Assert.Single(rebuilt.MusicClips);
    }
}
