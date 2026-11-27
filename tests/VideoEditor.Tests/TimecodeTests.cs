using VideoEditor.Core.Editing;
using VideoEditor.Core.Export;
using VideoEditor.Core.Playback;

namespace VideoEditor.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData(0, 30, "00:00:00:00")]
    [InlineData(65.4, 30, "00:01:05:12")]
    [InlineData(3723.5, 25, "01:02:03:12")]
    [InlineData(1 - 1 / 30.0, 30, "00:00:00:29")]   // 一秒裡最後一格
    [InlineData(1 - 1 / 60.0, 60, "00:00:00:59")]
    [InlineData(0.999, 29.97, "00:00:00:29")]        // 29.97 fps 一秒最多 0～29 格
    [InlineData(10, 29.97, "00:00:10:00")]           // 時分秒照實際時間走，不累積誤差
    [InlineData(-3, 30, "00:00:00:00")]
    [InlineData(2.5, 0, "00:00:02:15")]              // 圖片沒有影格率，以 30 fps 計
    public void FormatShowsFramesWithinTheSecond(double seconds, double fps, string expected) =>
        Assert.Equal(expected, Timecode.Format(seconds, fps));

    [Fact]
    public void DisplayFollowsTheChosenMode()
    {
        Assert.Equal("00:01:05:12", Timecode.Display(65.4, 30, TimeDisplay.Timecode));
        Assert.Equal("00:01:05.40", Timecode.Display(65.4, 30, TimeDisplay.Decimal));
    }

    [Theory]
    [InlineData("65", 65)]
    [InlineData("65.5", 65.5)]
    [InlineData("1:05", 65)]
    [InlineData("1:05.5", 65.5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("00:01:05:12", 65.4)]
    [InlineData("120f", 4)]
    [InlineData("120F", 4)]
    [InlineData(" 1 : 05 ", 65)]
    [InlineData("１：０５", 65)]       // 全形數字與冒號
    [InlineData("１：０５．５", 65.5)]
    [InlineData("1、05", 65)]          // 中文輸入法的頓號
    [InlineData("90:00", 5400)]        // 第一段可以超過 60
    public void AbsoluteTimes(string text, double expected)
    {
        var result = Timecode.Parse(text, 30, current: 100);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Seconds!.Value, 6);
    }

    [Theory]
    [InlineData("+10", 110)]
    [InlineData("-2.5", 97.5)]
    [InlineData("+12f", 100.4)]
    [InlineData("-1:00", 40)]
    [InlineData("＋１０", 110)]
    [InlineData("-200", -100)]   // 超出範圍由呼叫端處理
    public void RelativeTimesMoveFromTheCurrentPosition(string text, double expected) =>
        Assert.Equal(expected, Timecode.Parse(text, 30, current: 100).Seconds!.Value, 6);

    [Theory]
    [InlineData(29.97, "00:00:10:15")]
    [InlineData(25, "00:00:10:20")]
    [InlineData(60, "00:00:10:45")]
    public void FramesUseTheGivenRate(double fps, string text)
    {
        var seconds = Timecode.Parse(text, fps, 0).Seconds!.Value;
        Assert.Equal(text, Timecode.Format(seconds, fps));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(29.97)]
    [InlineData(25)]
    [InlineData(60)]
    public void FormatAndParseRoundTripOnEveryFrame(double fps)
    {
        for (var frame = 0; frame < 200; frame += 7)
        {
            var text = Timecode.Format(frame / fps, fps);
            var seconds = Timecode.Parse(text, fps, 0).Seconds!.Value;
            Assert.Equal(text, Timecode.Format(seconds, fps));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1::2")]
    [InlineData(":05")]
    [InlineData("1:2:3:4:5")]
    [InlineData("+")]
    [InlineData("1:75")]          // 秒數超過 60
    [InlineData("1:75:00")]       // 分鐘超過 60
    [InlineData("00:00:01:30")]   // 30 fps 沒有第 30 格
    [InlineData("00:00:01.5:10")] // 有格數時秒不能有小數
    [InlineData("1.5:00")]        // 只有秒可以有小數
    [InlineData("12.5f")]
    public void InvalidInputExplainsWhy(string text)
    {
        var result = Timecode.Parse(text, 30, 0);
        Assert.Null(result.Seconds);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void FrameErrorMentionsTheLimit() =>
        Assert.Contains("30", Timecode.Parse("00:00:01:30", 30, 0).Error);
}

public class VolumeTextTests
{
    [Theory]
    [InlineData(100, "原音量")]
    [InlineData(0, "靜音")]
    [InlineData(150, "+3.5 dB")]
    [InlineData(200, "+6.0 dB")]
    [InlineData(50, "-6.0 dB")]
    [InlineData(99.6, "原音量")]  // 滑桿取整數百分比
    public void DescribeShowsDecibels(double percent, string expected) =>
        Assert.Equal(expected, VolumeText.Describe(percent));

    [Theory]
    [InlineData("150", 150)]
    [InlineData("150%", 150)]
    [InlineData(" 72.5 ", 73)]
    [InlineData("０", 0)]
    [InlineData("２００％", 200)]
    public void ParsePercentAcceptsCommonForms(string text, double expected) =>
        Assert.Equal(expected, VolumeText.ParsePercent(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("201")]
    [InlineData("-5")]
    public void ParsePercentRejectsOutOfRange(string text) => Assert.Null(VolumeText.ParsePercent(text));
}

public class ExportNamingTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 9, 5, 0);

    [Theory]
    [InlineData(@"C:\影片\旅行.mp4", "旅行_剪輯_20261127-0905.mp4")]
    [InlineData("/home/a/trip.mov", "trip_剪輯_20261127-0905.mp4")]
    [InlineData(null, "剪輯_20261127-0905.mp4")]
    [InlineData(@"C:\a\...mp4", "剪輯_20261127-0905.mp4")]  // 去掉結尾句點後沒有名字
    [InlineData(@"C:\a\name. .mp4", "name_剪輯_20261127-0905.mp4")]
    public void DefaultFileNameUsesTheFirstClip(string? path, string expected) =>
        Assert.Equal(expected, ExportNaming.DefaultFileName(path, Now));

    [Fact]
    public void InvalidCharactersAreReplaced() =>
        Assert.Equal("a_b_c_d_剪輯_20261127-0905.mp4", ExportNaming.DefaultFileName("/x/a<b|c?d.mp4", Now));

    [Fact]
    public void LongNamesAreShortened()
    {
        var name = ExportNaming.DefaultFileName("/x/" + new string('長', 300) + ".mp4", Now);
        Assert.Equal(80 + "_剪輯_20261127-0905.mp4".Length, name.Length);
    }

    [Fact]
    public void FolderPrefersTheLastExportThenTheFirstClip()
    {
        var output = Path.Combine("disk", "out");
        var clips = Path.Combine("disk", "clips");
        var clip = Path.Combine(clips, "a.mp4");
        bool Exists(string path) => path == output || path == clips;
        Assert.Equal(output, ExportNaming.DefaultFolder(output, clip, Exists));
        Assert.Equal(clips, ExportNaming.DefaultFolder(Path.Combine("disk", "gone"), clip, Exists));
        Assert.Null(ExportNaming.DefaultFolder(null, null, Exists));
        Assert.Null(ExportNaming.DefaultFolder(null, Path.Combine("gone", "a.mp4"), Exists));
    }
}

public class ShortcutListTests
{
    [Fact]
    public void EveryPreviewKeyIsListed()
    {
        foreach (var key in Enum.GetValues<PreviewKey>())
            Assert.Contains(Shortcuts.All, s => s.Keys == Shortcuts.KeysText(key));
    }

    [Fact]
    public void GroupsStayTogether()
    {
        var groups = Shortcuts.All.Select(s => s.Group).ToList();
        var order = groups.Distinct().ToList();
        Assert.Equal(order, groups.Where((g, i) => i == 0 || groups[i - 1] != g).ToList());
    }
}

public class ReviewFixTests
{
    [Theory]
    [InlineData("10", 29.97, "00:00:10:00")]
    [InlineData("00:00:10:15", 29.97, "00:00:10:15")]
    [InlineData("00:00:10:29", 29.97, "00:00:10:29")]
    [InlineData("1:05", 30, "00:01:05:00")]
    [InlineData("00:00:03:07", 25, "00:00:03:07")]
    public void TypedTimecodeStaysOnTheSameFrameAfterAlignment(string text, double fps, string expected)
    {
        // 剪輯預覽輸入時間後會對齊到影格；29.97 fps 時往下取會跳到前一格（code review 發現）。
        var seconds = Timecode.Parse(text, fps, 0).Seconds!.Value;
        Assert.Equal(expected, Timecode.Format(Timecode.AlignToFrame(seconds, fps), fps));
    }

    [Fact]
    public void HalfPercentRoundsTheSameWayAsTheInputBox()
    {
        Assert.Equal(1, VolumeText.ParsePercent("0.5"));
        Assert.NotEqual("靜音", VolumeText.Describe(0.5));
    }

    [Fact]
    public void SidePanelsShrinkProportionallyWhenTheyDoNotFit()
    {
        // 上次在大螢幕存了 600 與 900，現在視窗只剩 1320 可用：中間至少 400，兩欄共 920。
        var (left, right) = VideoEditor.Core.Settings.PanelLayout.FitSides(600, 900, 1320, 200, 260, 400);
        Assert.Equal(920, left!.Value + right!.Value, 6);
        Assert.Equal(600 / 900.0, left.Value / right.Value, 6);
    }

    [Fact]
    public void SidePanelsThatFitAreKeptAndMissingOnesStayDefault()
    {
        Assert.Equal((300.0, 320.0), VideoEditor.Core.Settings.PanelLayout.FitSides(300, 320, 1320, 200, 260, 400));
        Assert.Equal((null, null), VideoEditor.Core.Settings.PanelLayout.FitSides(null, null, 900, 200, 260, 400));
    }

    [Fact]
    public void SidePanelsNeverGoBelowTheirMinimum()
    {
        var (left, right) = VideoEditor.Core.Settings.PanelLayout.FitSides(1000, 1000, 500, 200, 260, 400);
        Assert.Equal((200.0, 260.0), (left, right));
    }

    [Theory]
    [InlineData(254, 700, 254)]  // 放得下
    [InlineData(600, 700, 400)]  // 上方保住 300
    [InlineData(600, 400, 210)]  // 視窗太小，不小於時間軸最小高度
    public void TimelineHeightLeavesRoomForThePreview(double saved, double available, double expected) =>
        Assert.Equal(expected, VideoEditor.Core.Settings.PanelLayout.FitBottom(saved, available, 210, 300));
}
