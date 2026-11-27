using VideoEditor.Core.Ffmpeg;

namespace VideoEditor.Tests;

/// <summary>FFmpeg 的搜尋順序與信任來源（S-1）。</summary>
public sealed class FfmpegLocatorTests : IDisposable
{
    // 放在目前工作目錄底下，這樣才能用「相對於工作目錄」的路徑指到它。
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, "locator-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MakeTools(params string[] parts)
    {
        var directory = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(directory, "ffprobe.exe"), "");
        return directory;
    }

    private string EmptyBase() => Directory.CreateDirectory(Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N")[..6])).FullName;

    [Fact]
    public void PreferredDirectoryComesFirst()
    {
        var chosen = MakeTools("chosen");
        var appDirectory = EmptyBase();
        Directory.CreateDirectory(Path.Combine(appDirectory, "tools", "ffmpeg"));
        File.WriteAllText(Path.Combine(appDirectory, "tools", "ffmpeg", "ffmpeg.exe"), "");
        File.WriteAllText(Path.Combine(appDirectory, "tools", "ffmpeg", "ffprobe.exe"), "");

        var found = FfmpegLocator.Locate(appDirectory, MakeTools("on-path"), chosen);

        Assert.Equal(Path.Combine(chosen, "ffmpeg.exe"), found.Ffmpeg);
    }

    [Fact]
    public void MissingPreferredDirectoryFallsBackToTheUsualOrder()
    {
        var onPath = MakeTools("fallback");
        var found = FfmpegLocator.Locate(EmptyBase(), onPath, Path.Combine(_root, "deleted-folder"));
        Assert.Equal(Path.Combine(onPath, "ffmpeg.exe"), found.Ffmpeg);
    }

    [Fact]
    public void RelativePreferredDirectoryIsIgnored()
    {
        // 相對路徑依工作目錄解析，和 PATH 的相對項目一樣不信任。
        MakeTools("relative-choice");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_root, "relative-choice"));
        Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(EmptyBase(), "", relative));
    }

    [Fact]
    public void ContainsToolsNeedsBothProgramsAndAFullPath()
    {
        var both = MakeTools("both");
        var onlyOne = Path.Combine(_root, "only-one");
        Directory.CreateDirectory(onlyOne);
        File.WriteAllText(Path.Combine(onlyOne, "ffmpeg.exe"), "");

        Assert.True(FfmpegLocator.ContainsTools(both));
        Assert.False(FfmpegLocator.ContainsTools(onlyOne));
        Assert.False(FfmpegLocator.ContainsTools(Path.GetRelativePath(Environment.CurrentDirectory, both)));
    }

    [Fact]
    public void SearchedPlacesListTheChosenFolderFirst()
    {
        var places = FfmpegLocator.SearchedPlaces("/app", "/chosen");
        Assert.Contains("/chosen", places[0]);
        Assert.Equal(4, places.Count);
        Assert.Equal(3, FfmpegLocator.SearchedPlaces("/app").Count);
    }

    [Fact]
    public void FindsToolsInAFullyQualifiedPathEntry()
    {
        var tools = MakeTools("on-path");

        var found = FfmpegLocator.Locate(EmptyBase(), tools);

        Assert.Equal(Path.Combine(tools, "ffmpeg.exe"), found.Ffmpeg);
        Assert.Equal(Path.Combine(tools, "ffprobe.exe"), found.Ffprobe);
    }

    [Fact]
    public void RelativePathEntriesAreIgnored()
    {
        // 這個資料夾放了 FFmpeg，但 PATH 寫的是相對於工作目錄的路徑：不能因此就信任它。
        var tools = MakeTools("relative-bin");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, tools);
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.True(File.Exists(Path.Combine(relative, "ffmpeg.exe"))); // 沒有被忽略的話，確實找得到

        Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(EmptyBase(), relative));
    }

    [Fact]
    public void DotAndEmptyEntriesAreIgnored()
    {
        var separator = Path.PathSeparator;
        Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(EmptyBase(), $".{separator}{separator}  {separator}tools"));
    }

    [Fact]
    public void QuotedEntriesAreUnwrapped()
    {
        // Windows 的 PATH 常見 "C:\Program Files\ffmpeg\bin" 這種帶引號的寫法。
        var tools = MakeTools("quoted path");

        var found = FfmpegLocator.Locate(EmptyBase(), $"\"{tools}\"");

        Assert.Equal(Path.Combine(tools, "ffmpeg.exe"), found.Ffmpeg);
    }

    [Fact]
    public void ProgramFolderComesBeforePath()
    {
        var onPath = MakeTools("elsewhere");
        var appDirectory = Directory.CreateDirectory(Path.Combine(_root, "app-first")).FullName;
        var bundled = MakeTools("app-first", "tools", "ffmpeg");

        var found = FfmpegLocator.Locate(appDirectory, onPath);

        Assert.Equal(Path.Combine(bundled, "ffmpeg.exe"), found.Ffmpeg);
    }

    [Fact]
    public void BothExecutablesMustBeInTheSameFolder()
    {
        var onlyOne = Directory.CreateDirectory(Path.Combine(_root, "only-ffmpeg")).FullName;
        File.WriteAllText(Path.Combine(onlyOne, "ffmpeg.exe"), "");

        Assert.Throws<FfmpegNotFoundException>(() => FfmpegLocator.Locate(EmptyBase(), onlyOne));
    }

    [Fact]
    public void ARelativeEntryDoesNotHideALaterGoodOne()
    {
        var good = MakeTools("good");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, MakeTools("bad"));

        var found = FfmpegLocator.Locate(EmptyBase(), $"{relative}{Path.PathSeparator}{good}");

        Assert.Equal(Path.Combine(good, "ffmpeg.exe"), found.Ffmpeg);
    }
}

public class FfmpegVersionTests
{
    [Theory]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers\nbuilt with gcc 13", "ffmpeg version 6.1.1-3ubuntu5")]
    [InlineData("ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers", "ffmpeg version 9.0.2-essentials_build-www.gyan.dev")]
    [InlineData("ffmpeg version N-118000-gabcdef\r\nbuilt with ...", "ffmpeg version N-118000-gabcdef")] // 直接從 git 建置：沒有版本號
    [InlineData("ffmpeg version 7.0", "ffmpeg version 7.0")]
    public void KeepsTheVersionAndDropsTheCopyright(string output, string expected) =>
        Assert.Equal(expected, FfmpegVersion.FirstLine(output));

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void NoOutputMeansNoVersion(string output) => Assert.Null(FfmpegVersion.FirstLine(output));
}

[Collection("media")]
[Trait("Category", "Integration")]
public class FfmpegVersionIntegrationTests(TestMedia media)
{
    [Fact]
    public async Task ReadsTheVersionOfTheRealFfmpeg()
    {
        var version = await FfmpegVersion.ReadAsync(media.Tools);

        Assert.NotNull(version);
        Assert.StartsWith("ffmpeg version", version);
        Assert.DoesNotContain("Copyright", version);
    }

    [Fact]
    public async Task ReturnsNullInsteadOfThrowingWhenFfmpegCannotRun()
    {
        var missing = new FfmpegTools(Path.Combine(media.Directory, "no-such-ffmpeg.exe"), "x");

        Assert.Null(await FfmpegVersion.ReadAsync(missing));
    }
}
