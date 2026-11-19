using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>濾鏡圖放進檔案的規則（純函式）。</summary>
public class FilterGraphFileRuleTests
{
    private static readonly string[] Args = ["-y", "-i", "a.mp4", "-filter_complex", "[0:v]scale=1280:720[vout]", "-map", "[vout]"];

    [Fact]
    public void MoveReplacesTheGraphWithTheOptionAndPath()
    {
        var (moved, graph) = FilterGraphFile.Move(Args, FilterGraphFile.NewOption, "work/filter.txt");

        Assert.Equal(["-y", "-i", "a.mp4", "-/filter_complex", "work/filter.txt", "-map", "[vout]"], moved);
        Assert.Equal("[0:v]scale=1280:720[vout]", graph);
    }

    [Fact]
    public void MoveSupportsTheLegacyOption()
    {
        var (moved, _) = FilterGraphFile.Move(Args, FilterGraphFile.LegacyOption, "f.txt");
        Assert.Equal(["-y", "-i", "a.mp4", "-filter_complex_script", "f.txt", "-map", "[vout]"], moved);
    }

    [Fact]
    public void MoveDoesNotChangeTheOriginalArguments()
    {
        var copy = Args.ToArray();
        FilterGraphFile.Move(Args, FilterGraphFile.NewOption, "f.txt");
        Assert.Equal(copy, Args);
    }

    [Theory]
    [InlineData("-y", "-i", "a.mp4")]
    [InlineData("-i", "a.mp4", "-filter_complex")] // 後面沒有值
    public void MoveRejectsArgumentsWithoutAGraph(params string[] args) =>
        Assert.Throws<InvalidOperationException>(() => FilterGraphFile.Move(args, FilterGraphFile.NewOption, "f.txt"));

    [Fact]
    public void FileIsUsedOnlyWhenTheCommandIsLongAndHasAGraph()
    {
        var length = FilterGraphFile.CommandLength(Args);
        Assert.False(FilterGraphFile.ShouldUseFile(Args, length)); // 剛好等於門檻不算超過
        Assert.True(FilterGraphFile.ShouldUseFile(Args, length - 1));
        Assert.True(FilterGraphFile.ShouldUseFile(Args, 0));
        Assert.False(FilterGraphFile.ShouldUseFile(["-y", "-i", "a.mp4"], 0)); // 沒有濾鏡圖就沒有東西可搬
    }

    [Fact]
    public void CommandLengthCountsQuotesAndSpaces() =>
        Assert.Equal(3 + 4 + 5, FilterGraphFile.CommandLength(["", "a", "ab"])); // 每個參數多算 3 個字元（引號與空白）

    [Theory]
    [InlineData(8, 400)]   // 路徑短時，上限遠高於原本的約 89 個
    [InlineData(55, 200)]  // 路徑長時，上限也高於原本的約 78 個
    public void ManyMoreClipsFitNowThatTheGraphMovesToAFile(int pathLength, int clips)
    {
        var media = new MediaInfo
        {
            Path = "C:\\" + new string('x', pathLength - 7) + ".mp4", Duration = 6000, HasVideo = true, HasAudio = true,
            DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p", BitDepth = 8,
        };
        var t = new Timeline();
        for (var i = 0; i < clips; i++) t.Append(media, i, i + 1);

        var plan = ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill), @"C:\Users\someone\Videos\out.mp4");

        // 整條命令列已經超過原本的上限，要靠濾鏡圖檔案才放得下。
        Assert.True(FilterGraphFile.CommandLength(plan.Arguments) > 30000);
        Assert.True(FilterGraphFile.ShouldUseFile(plan.Arguments, ExportService.DefaultGraphFileThreshold));
    }

    [Fact]
    public void FarTooManyClipsStillFailBeforeExporting()
    {
        var media = new MediaInfo
        {
            Path = "a.mp4", Duration = 6000, HasVideo = true, HasAudio = true,
            DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
        };
        var t = new Timeline();
        for (var i = 0; i < 2000; i++) t.Append(media, i, i + 1);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExportPlanner.Build(t, new OutputSettings(AspectRatio.R16x9, FitMode.Fill), "o.mp4"));
        Assert.Contains("命令列長度上限", error.Message);
    }
}

/// <summary>濾鏡圖檔案的實際匯出（需要 FFmpeg）。</summary>
[Collection("media")]
[Trait("Category", "Integration")]
public class FilterGraphFileExportTests(TestMedia media)
{
    private static readonly OutputSettings Default = new(AspectRatio.R16x9, FitMode.Fill);

    [Fact]
    public async Task OneOfTheTwoSyntaxesIsChosenAndItWorks()
    {
        var option = await FilterGraphFile.OptionAsync(media.Tools);

        Assert.Contains(option, new[] { FilterGraphFile.NewOption, FilterGraphFile.LegacyOption });
        Assert.Same(await FilterGraphFile.OptionAsync(media.Tools), option); // 只探測一次
    }

    [Fact]
    public async Task ForcedFileExportMatchesTheInlineExport()
    {
        var source = await media.Probe(media.Landscape);
        var t = new Timeline();
        t.Append(source, 0.5, 1.5);
        t.Append(source, 2, 3);
        var directory = Path.Combine(media.Directory, "graph-file");
        Directory.CreateDirectory(directory);
        var inline = Path.Combine(directory, "inline.mp4");
        var viaFile = Path.Combine(directory, "via-file.mp4");

        await new ExportService(media.Tools).ExportAsync(ExportPlanner.Build(t, Default, inline), null);
        var commands = new List<List<string>>();
        void OnStarted(System.Diagnostics.Process process) { lock (commands) commands.Add(process.StartInfo.ArgumentList.ToList()); }
        ProcessRunner.Started += OnStarted;
        try
        {
            await new ExportService(media.Tools, graphFileThreshold: 0).ExportAsync(ExportPlanner.Build(t, Default, viaFile), null);
        }
        finally { ProcessRunner.Started -= OnStarted; }

        // 真的用了檔案：最後的編碼命令沒有行內濾鏡圖，而是用探測到的選項讀檔案。
        var option = await FilterGraphFile.OptionAsync(media.Tools);
        var encode = Assert.Single(commands, c => c.Contains("-movflags"));
        Assert.DoesNotContain("-filter_complex", encode);
        Assert.Contains(option, encode);

        // 結果與行內濾鏡圖相同，工作資料夾也已刪除，只剩兩個輸出檔。
        Assert.Equal(await media.CountFrames(inline), await media.CountFrames(viaFile));
        Assert.Equal(60, await media.CountFrames(viaFile));
        Assert.Equal(["inline.mp4", "via-file.mp4"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task ClipCountBeyondTheOldLimitExports()
    {
        // 120 個 0.2 秒的片段：整條命令列遠超過原本 30,000 字元的上限，會自動改用濾鏡圖檔案。
        var source = await media.Probe(media.Landscape);
        var t = new Timeline();
        for (var i = 0; i < 120; i++) t.Append(source, (i % 90) / 30.0, (i % 90) / 30.0 + 0.2);
        var output = media.Output("many-clips.mp4");
        var plan = ExportPlanner.Build(t, Default, output);
        Assert.True(FilterGraphFile.CommandLength(plan.Arguments) > 30000);

        await new ExportService(media.Tools).ExportAsync(plan, null);

        Assert.Equal(120 * 6, await media.CountFrames(output));
    }
}
