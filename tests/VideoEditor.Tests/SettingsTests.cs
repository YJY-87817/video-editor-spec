using VideoEditor.Core.Models;
using VideoEditor.Core.Settings;

namespace VideoEditor.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VideoEditorSettingsTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.True(settings.SnapToSilence);
        Assert.True(settings.SnapToClips);
        Assert.Equal(AspectRatio.Original, settings.Ratio);
        Assert.Equal(FitMode.Fill, settings.Mode);
        Assert.Null(settings.Window);
    }

    [Fact]
    public void SavedSettingsRoundTripAndTheFolderIsCreated()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new AppSettings
        {
            Window = new WindowBounds(10, 20, 1400, 900),
            WindowMaximized = true,
            LeftPanelWidth = 300,
            RightPanelWidth = 320,
            TimelineHeight = 280,
            SnapToSilence = false,
            SnapToClips = false,
            Ratio = AspectRatio.R9x16,
            Mode = FitMode.Pad,
            LastImportFolder = @"D:\影片",
            LastExportFolder = @"E:\輸出",
        };

        Assert.True(store.Save(settings));

        Assert.Equal(settings, new SettingsStore(SettingsPath).Load());
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void ModeIsStoredByName()
    {
        new SettingsStore(SettingsPath).Save(new AppSettings { Mode = FitMode.Pad });

        Assert.Contains("\"Pad\"", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("{\"Ratio\": {\"W\": 7, \"H\": 3}}")]   // 不是程式提供的比例
    [InlineData("{\"Ratio\": null}")]
    [InlineData("{\"Mode\": 9}")]                     // 列舉值超出範圍
    public void EditedValuesOutsideTheChoicesFallBackToDefaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(AspectRatio.Original, settings.Ratio);
        Assert.Equal(FitMode.Fill, settings.Mode);
    }

    [Fact]
    public void PresetRatioIsKept()
    {
        new SettingsStore(SettingsPath).Save(new AppSettings { Ratio = AspectRatio.R4x5 });

        Assert.Equal(AspectRatio.R4x5, new SettingsStore(SettingsPath).Load().Ratio);
    }

    [Theory]
    [InlineData("這不是 JSON")]
    [InlineData("{\"Mode\": \"NoSuchMode\"}")]
    [InlineData("{\"SnapToClips\": \"yes\"}")]
    [InlineData("null")]
    [InlineData("")]
    public void BrokenFileGivesDefaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, content);

        Assert.Equal(new AppSettings(), new SettingsStore(SettingsPath).Load());
    }

    [Fact]
    public void UnknownFieldsAreIgnoredAndMissingFieldsUseDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{\"Mode\": \"Pad\", \"SomethingFromTheFuture\": 42}");

        var settings = new SettingsStore(SettingsPath).Load();

        Assert.Equal(FitMode.Pad, settings.Mode);
        Assert.True(settings.SnapToSilence);
        Assert.Equal(AspectRatio.Original, settings.Ratio);
    }

    [Fact]
    public void SaveFailureReturnsFalseWithoutThrowing()
    {
        // 設定檔的位置被同名資料夾佔住，無法寫入。
        Directory.CreateDirectory(SettingsPath);

        Assert.False(new SettingsStore(SettingsPath).Save(new AppSettings()));
    }

    [Theory]
    [InlineData(100, 100, 1200, 800, true)]
    [InlineData(-300, 100, 1200, 800, true)]   // 左邊超出，但還有一大塊在畫面內
    [InlineData(1800, 100, 1200, 800, true)]   // 右邊超出，畫面內還剩 120 像素
    [InlineData(1830, 100, 1200, 800, false)]  // 只剩 90 像素，不夠抓
    [InlineData(2000, 100, 1200, 800, false)]  // 整個在右邊的螢幕外（外接螢幕拔掉）
    [InlineData(100, -10, 1200, 800, false)]   // 標題列在畫面上方，拖不到
    [InlineData(100, 1060, 1200, 800, false)]  // 標題列在畫面下方
    [InlineData(100, 100, 0, 800, false)]
    public void WindowMustKeepItsTitleBarOnScreen(double left, double top, double width, double height, bool expected)
    {
        // 1920×1080 的單一螢幕。
        Assert.Equal(expected, new WindowBounds(left, top, width, height).IsReachableOn(0, 0, 1920, 1080));
    }

    [Fact]
    public void WindowOnASecondMonitorToTheLeftIsReachable()
    {
        // 虛擬螢幕從 -1920 開始（主螢幕左邊還有一個螢幕）。
        Assert.True(new WindowBounds(-1500, 50, 1200, 800).IsReachableOn(-1920, 0, 3840, 1080));
        Assert.False(new WindowBounds(-1500, 50, 1200, 800).IsReachableOn(0, 0, 1920, 1080));
    }
}
