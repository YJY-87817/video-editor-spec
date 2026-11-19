using System.ComponentModel;
using System.Text.Json;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>健檢報告 S-5、Q-8、W-6：不需要 FFmpeg 的檢查。</summary>
public class PreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoEditorTests", Guid.NewGuid().ToString("N"));

    public PreflightTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Timeline TimelineOf(string source)
    {
        File.WriteAllBytes(source, []);
        var timeline = new Timeline();
        timeline.Append(new MediaInfo { Path = source, Duration = 5, HasVideo = true, DisplayWidth = 1280, DisplayHeight = 720, FrameRate = new Rational(30, 1) }, 0, 1);
        return timeline;
    }

    [Fact]
    public void InvalidPathIsReportedNotThrown()
    {
        var message = ExportService.Preflight(new Timeline(), "bad\0name.mp4");
        Assert.NotNull(message);
        Assert.Contains("輸出路徑不合法", message);
    }

    [Fact]
    public void WritableLocationPassesAndLeavesNoProbeFile()
    {
        var source = Path.Combine(_root, "in.mp4");
        Assert.Null(ExportService.Preflight(TimelineOf(source), Path.Combine(_root, "out.mp4")));
        Assert.DoesNotContain(Directory.GetFiles(_root), f => Path.GetFileName(f).StartsWith(".write-test-"));
    }

    [Fact]
    public void SameFileIsRejected()
    {
        var source = Path.Combine(_root, "in.mp4");
        Assert.Contains("不能與來源素材相同", ExportService.Preflight(TimelineOf(source), Path.Combine(_root, ".", "in.mp4")));
    }

    [Fact]
    public void FileInsideLinkedDirectoryIsRejected()
    {
        var folder = Path.Combine(_root, "videos");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "in.mp4");
        var timeline = TimelineOf(source);
        var link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // 沒有建立連結的權限時略過。
        Assert.Contains("不能與來源素材相同", ExportService.Preflight(timeline, Path.Combine(link, "in.mp4")));
    }

    [Fact]
    public void SymbolicLinkToSourceIsRejected()
    {
        var source = Path.Combine(_root, "in.mp4");
        var timeline = TimelineOf(source);
        var link = Path.Combine(_root, "link.mp4");
        try { File.CreateSymbolicLink(link, source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // 沒有建立連結的權限（Windows 一般使用者）時略過。
        Assert.Contains("不能與來源素材相同", ExportService.Preflight(timeline, link));
    }
}

public class ExpectedErrorsTests
{
    [Fact]
    public void ClassifiesFileAndProcessFailures()
    {
        Assert.True(ExpectedErrors.IsFile(new IOException()));
        Assert.True(ExpectedErrors.IsFile(new UnauthorizedAccessException()));
        Assert.False(ExpectedErrors.IsFile(new Win32Exception()));

        Assert.True(ExpectedErrors.IsProcess(new FfmpegException("x", "")));
        Assert.True(ExpectedErrors.IsProcess(new Win32Exception()));
        Assert.False(ExpectedErrors.IsProcess(new IOException()));

        Assert.True(ExpectedErrors.IsFileOrProcess(new IOException()));
        Assert.True(ExpectedErrors.IsFileOrProcess(new Win32Exception()));
    }

    [Fact]
    public void ProgrammingErrorsAreNotExpected()
    {
        foreach (Exception e in new Exception[] { new ArgumentException(), new InvalidOperationException(), new NullReferenceException() })
        {
            Assert.False(ExpectedErrors.IsFileOrProcess(e));
            Assert.False(ExpectedErrors.IsProbe(e));
        }
    }

    [Fact]
    public void ProbeAlsoAcceptsBadOutput()
    {
        Assert.True(ExpectedErrors.IsProbe(new InvalidDataException()));
        Assert.True(ExpectedErrors.IsProbe(new JsonException()));
    }
}

public class ReentryGuardTests
{
    [Fact]
    public void ActiveOnlyInsideTheScope()
    {
        var guard = new ReentryGuard();
        Assert.False(guard.Active);
        using (guard.Enter()) Assert.True(guard.Active);
        Assert.False(guard.Active);
    }

    [Fact]
    public void ReleasedEvenWhenTheBlockThrows()
    {
        var guard = new ReentryGuard();
        void Fail()
        {
            using var _ = guard.Enter();
            throw new InvalidOperationException();
        }

        Assert.Throws<InvalidOperationException>(Fail);
        Assert.False(guard.Active);
    }
}
