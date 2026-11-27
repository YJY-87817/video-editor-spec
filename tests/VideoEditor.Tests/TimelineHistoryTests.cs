using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

public class TimelineHistoryTests
{
    private static MediaInfo Video(string path = "a.mp4", double duration = 10) => new()
    {
        Path = path, Duration = duration, HasVideo = true, HasAudio = true,
        DisplayWidth = 1920, DisplayHeight = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p",
    };

    private static MediaInfo Music() => new() { Path = "m.m4a", Duration = 20, HasAudio = true };

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>兩段影片、一段配樂的時間軸，與它的復原紀錄。</summary>
    private static (Timeline Timeline, TimelineHistory History, ManualTime Time, TimelineClip First, TimelineClip Second, TimelineClip Song) Setup(
        int capacity = TimelineHistory.DefaultCapacity)
    {
        var timeline = new Timeline();
        var first = timeline.Append(Video("a.mp4"), 0, 4);
        var second = timeline.Append(Video("b.mp4"), 1, 3);
        var song = timeline.Append(Music(), 0, 8);
        var time = new ManualTime();
        return (timeline, new TimelineHistory(timeline, capacity, time), time, first, second, song);
    }

    /// <summary>做一個修改並記錄；回傳修改前的樣子，方便比對復原結果。</summary>
    private static TimelineSnapshot Do(TimelineHistory history, string label, Action change, string? mergeKey = null)
    {
        var before = history.Capture();
        change();
        history.Commit(before, label, mergeKey);
        return before;
    }

    public static TheoryData<string> Operations => ["加入", "刪除", "分割", "移動", "修剪開頭", "修剪結尾", "交換順序", "旋轉", "畫面位置", "倒放", "音量"];

    private static void Apply(string operation, Timeline timeline, TimelineClip first, TimelineClip second, TimelineClip song)
    {
        switch (operation)
        {
            case "加入": timeline.Append(Video("c.mp4"), 2, 5); break;
            case "刪除": timeline.Remove(first); break;
            case "分割": timeline.Split(first, 1.5); break;
            case "移動": timeline.Move(second, 9); break;
            case "修剪開頭": timeline.TrimStart(first, 1); break;
            case "修剪結尾": timeline.TrimEnd(second, 5.5); break;
            case "交換順序": timeline.Swap(first, 1); break;
            case "旋轉": timeline.SetTransform(first, first.Transform.Rotate(90)); break;
            case "畫面位置": timeline.SetFraming(first, new ClipFraming(1.5, 0.4, 0.5)); break;
            case "倒放": timeline.SetReversed(second, true); break;
            case "音量": song.Volume = 0.3; break;
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void UndoRestoresAndRedoReappliesEveryOperation(string operation)
    {
        var (timeline, history, _, first, second, song) = Setup();
        var before = history.Capture();

        Apply(operation, timeline, first, second, song);
        var after = history.Capture();
        Assert.NotEqual(before, after);
        Assert.True(history.Commit(before, operation));

        Assert.Equal(operation, history.Undo());
        Assert.Equal(before, history.Capture());
        Assert.Equal(operation, history.Redo());
        Assert.Equal(after, history.Capture());
    }

    [Fact]
    public void UndoKeepsTheSameClipObjects()
    {
        var (timeline, history, _, first, second, _) = Setup();
        Do(history, "刪除", () => timeline.Remove(first));

        history.Undo();

        Assert.Same(first, timeline.VideoClips[0]);
        Assert.Same(second, timeline.VideoClips[1]);
    }

    [Fact]
    public void NothingIsRecordedWhenNothingChanged()
    {
        var (_, history, _, _, _, _) = Setup();
        var changed = 0;
        history.Changed += () => changed++;

        Assert.False(history.Commit(history.Capture(), "沒有改變"));

        Assert.False(history.CanUndo);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void LabelsFollowTheStacks()
    {
        var (timeline, history, _, first, second, _) = Setup();
        Assert.Null(history.UndoLabel);
        Do(history, "刪除片段", () => timeline.Remove(first));
        Do(history, "倒放", () => timeline.SetReversed(second, true));

        Assert.Equal("倒放", history.UndoLabel);
        history.Undo();
        Assert.Equal("刪除片段", history.UndoLabel);
        Assert.Equal("倒放", history.RedoLabel);
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());
        Assert.Equal("刪除片段", history.RedoLabel);
    }

    [Fact]
    public void NewChangeClearsRedo()
    {
        var (timeline, history, _, first, second, _) = Setup();
        Do(history, "刪除", () => timeline.Remove(first));
        history.Undo();

        Do(history, "倒放", () => timeline.SetReversed(second, true));

        Assert.False(history.CanRedo);
        Assert.Null(history.Redo());
    }

    [Fact]
    public void OldestStepIsDroppedAtCapacity()
    {
        var (_, history, _, _, _, song) = Setup(capacity: 3);
        var original = history.Capture();
        foreach (var volume in new[] { 0.1, 0.2, 0.3, 0.4 }) Do(history, $"音量 {volume}", () => song.Volume = volume);

        Assert.Equal("音量 0.4", history.Undo());
        Assert.Equal("音量 0.3", history.Undo());
        Assert.Equal("音量 0.2", history.Undo());
        Assert.False(history.CanUndo);
        Assert.Equal(0.1, song.Volume); // 第一步已經被丟掉，回不到原本的 1.0
        Assert.NotEqual(original, history.Capture());
    }

    [Fact]
    public void ChangesWithTheSameMergeKeyInQuickSuccessionAreOneStep()
    {
        var (_, history, time, _, _, song) = Setup();
        var before = Do(history, "調整音量", () => song.Volume = 0.8, "volume");
        time.Now += TimeSpan.FromMilliseconds(300);
        Do(history, "調整音量", () => song.Volume = 0.5, "volume");
        time.Now += TimeSpan.FromMilliseconds(300);
        Do(history, "調整音量", () => song.Volume = 0.2, "volume");

        history.Undo();

        Assert.False(history.CanUndo);
        Assert.Equal(before, history.Capture());
        Assert.Equal(1.0, song.Volume);
    }

    [Fact]
    public void SameMergeKeyAfterAPauseIsANewStep()
    {
        var (_, history, time, _, _, song) = Setup();
        Do(history, "調整音量", () => song.Volume = 0.8, "volume");
        time.Now += TimelineHistory.MergeWindow + TimeSpan.FromMilliseconds(1);
        Do(history, "調整音量", () => song.Volume = 0.5, "volume");

        history.Undo();

        Assert.Equal(0.8, song.Volume);
        Assert.True(history.CanUndo);
    }

    [Fact]
    public void DifferentMergeKeysAreSeparateSteps()
    {
        var (_, history, _, first, _, song) = Setup();
        Do(history, "調整音量", () => song.Volume = 0.8, $"volume:{song.Id}");
        Do(history, "調整音量", () => first.Volume = 0.5, $"volume:{first.Id}");

        history.Undo();

        Assert.Equal(1.0, first.Volume);
        Assert.Equal(0.8, song.Volume);
    }

    [Fact]
    public void MergedStepThatEndsWhereItStartedIsRemoved()
    {
        var (_, history, _, _, _, song) = Setup();
        Do(history, "調整音量", () => song.Volume = 0.8, "volume");
        Do(history, "調整音量", () => song.Volume = 1.0, "volume");

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ChangeAfterUndoIsNotMergedIntoTheUndoneStep()
    {
        var (_, history, _, _, _, song) = Setup();
        Do(history, "調整音量", () => song.Volume = 0.8, "volume");
        Do(history, "調整音量", () => song.Volume = 0.6, "volume");
        history.Undo();

        Do(history, "調整音量", () => song.Volume = 0.4, "volume");
        history.Undo();

        Assert.Equal(1.0, song.Volume);
    }

    [Fact]
    public void SplitUndoRemovesTheRightPartAndRestoresTheLength()
    {
        var (timeline, history, _, first, _, _) = Setup();
        TimelineClip? right = null;
        Do(history, "分割", () => right = timeline.Split(first, 1.5));

        history.Undo();

        Assert.DoesNotContain(right!, timeline.VideoClips);
        Assert.Equal(4, first.Duration, 6);
        history.Redo();
        Assert.Same(right, timeline.VideoClips[1]);
        Assert.Equal(1.5, first.Duration, 6);
    }
}
