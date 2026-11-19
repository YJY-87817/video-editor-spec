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

public class TemporaryFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoEditorTests", Guid.NewGuid().ToString("N"));

    public TemporaryFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TemporaryFiles.DeleteDirectory(_root);

    [Fact]
    public void DeletesDirectoryWithContents()
    {
        var directory = Path.Combine(_root, "nested");
        Directory.CreateDirectory(Path.Combine(directory, "inner"));
        File.WriteAllText(Path.Combine(directory, "inner", "a.tmp"), "x");
        File.WriteAllText(Path.Combine(directory, "b.tmp"), "x");

        Assert.True(TemporaryFiles.DeleteDirectory(directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MissingDirectoryOrFileIsFine()
    {
        Assert.True(TemporaryFiles.DeleteDirectory(Path.Combine(_root, "never-created")));
        Assert.True(TemporaryFiles.DeleteFile(Path.Combine(_root, "never-created.tmp")));
    }

    [Fact]
    public void DeletesFile()
    {
        var file = Path.Combine(_root, "one.tmp");
        File.WriteAllText(file, "x");
        Assert.True(TemporaryFiles.DeleteFile(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void GivesUpWithoutThrowingWhenTheFileStaysLocked()
    {
        // Linux 允許刪除開著的檔案，這個情境只在 Windows 才成立。
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "locked.tmp");
        using var held = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
        Assert.False(TemporaryFiles.DeleteFile(file)); // 重試約 2 秒後放棄
        Assert.True(File.Exists(file));
    }
}

public class TimelineClipVolumeTests
{
    private static TimelineClip Clip()
    {
        var timeline = new Timeline();
        return timeline.Append(new MediaInfo { Path = "a.mp4", Duration = 5, HasVideo = true, HasAudio = true, DisplayWidth = 640, DisplayHeight = 360, FrameRate = new Rational(30, 1) }, 0, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void AcceptsGainsInRange(double gain)
    {
        var clip = Clip();
        clip.Volume = gain;
        Assert.Equal(gain, clip.Volume);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsGainsOutOfRange(double gain)
    {
        var clip = Clip();
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.Volume = gain);
        Assert.Equal(1.0, clip.Volume); // 值不變
    }

    [Fact]
    public void TimelineDurationIsTheLatestClipEndOnEitherTrack()
    {
        var timeline = new Timeline();
        Assert.Equal(0, timeline.Duration);
        var video = new MediaInfo { Path = "v.mp4", Duration = 5, HasVideo = true, DisplayWidth = 640, DisplayHeight = 360, FrameRate = new Rational(30, 1) };
        var music = new MediaInfo { Path = "m.m4a", Duration = 9, HasAudio = true };
        timeline.Append(video, 0, 3);
        Assert.Equal(3, timeline.Duration);
        timeline.Append(music, 0, 8);
        Assert.Equal(8, timeline.Duration);
        timeline.Append(video, 0, 2);
        Assert.Equal(8, timeline.Duration);
    }
}
