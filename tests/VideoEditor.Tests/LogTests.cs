using VideoEditor.Core.Logging;

namespace VideoEditor.Tests;

public sealed class LogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VideoEditorLogTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string[] Files() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "app-*.log") : [];

    [Fact]
    public void WritesLevelMessageAndException()
    {
        var log = new FileLog(_directory);
        log.Write("ERROR", "匯出失敗", new InvalidOperationException("boom"));

        var file = Assert.Single(Files());
        var text = File.ReadAllText(file);
        Assert.Contains("ERROR", text);
        Assert.Contains("匯出失敗", text);
        Assert.Contains("InvalidOperationException: boom", text);
    }

    [Fact]
    public void AppendsToTheSameDailyFile()
    {
        var log = new FileLog(_directory);
        log.Write("INFO", "第一行");
        log.Write("INFO", "第二行");

        var text = File.ReadAllText(Assert.Single(Files()));
        Assert.True(text.IndexOf("第一行") < text.IndexOf("第二行"));
    }

    [Fact]
    public void StartsANewFileWhenTheCurrentOneIsFull()
    {
        var log = new FileLog(_directory, maxBytes: 200);
        for (var i = 0; i < 10; i++) log.Write("INFO", new string('x', 100) + i);

        Assert.True(Files().Length > 1);
        // 每個檔案都不會比上限多出太多（最多多一筆紀錄）。
        Assert.All(Files(), file => Assert.True(new FileInfo(file).Length < 200 + 300));
    }

    [Fact]
    public void KeepsOnlyTheNewestFiles()
    {
        var log = new FileLog(_directory, maxBytes: 50, keep: 3);
        for (var i = 0; i < 12; i++) log.Write("INFO", new string('x', 100) + i);

        Assert.Equal(3, Files().Length);
        // 最新的紀錄一定還在。
        Assert.Contains(Files(), file => File.ReadAllText(file).Contains(new string('x', 100) + "11"));
    }

    [Fact]
    public void NeverThrowsWhenTheFolderCannotBeUsed()
    {
        // 資料夾的位置已經是一個檔案：建立資料夾與寫入都會失敗。
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "not a folder");
        try
        {
            var log = new FileLog(_directory);
            log.Write("ERROR", "寫不進去");
        }
        finally
        {
            File.Delete(_directory);
        }
    }
}
