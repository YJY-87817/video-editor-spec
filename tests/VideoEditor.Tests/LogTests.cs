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
    public void TheFileJustWrittenIsNeverPruned()
    {
        // 舊檔的修改時間比現在新（時鐘被調回、檔案系統時間精度不足），仍不能刪掉剛寫的當日檔。
        Directory.CreateDirectory(_directory);
        var future = DateTime.UtcNow.AddHours(1);
        for (var i = 0; i < 5; i++)
        {
            var old = Path.Combine(_directory, $"app-2000010{i}.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, future);
        }

        new FileLog(_directory, keep: 3).Write("INFO", "剛寫的紀錄");

        Assert.Contains(Files(), file => File.ReadAllText(file).Contains("剛寫的紀錄"));
        Assert.Equal(3, Files().Length);
    }

    [Fact]
    public void DropsEntriesBeyondTheLimitAndReportsHowManyLater()
    {
        long clock = 0;
        var log = new FileLog(_directory, maxPerWindow: 3, windowMs: 1000, ticks: () => clock);
        for (var i = 0; i < 10; i++) log.Write("ERROR", $"重複的錯誤 {i}");
        clock = 2000; // 下一個區間
        log.Write("ERROR", "之後的錯誤");

        var text = File.ReadAllText(Assert.Single(Files()));
        // 留下的是開頭的（原始原因），略過的是後面的。
        Assert.Contains("重複的錯誤 0", text);
        Assert.Contains("重複的錯誤 2", text);
        Assert.DoesNotContain("重複的錯誤 3", text);
        Assert.Contains("略過 7 筆", text);
        Assert.Contains("之後的錯誤", text);
    }

    [Fact]
    public void RecreatesTheFolderIfItIsDeletedWhileRunning()
    {
        long clock = 0;
        var log = new FileLog(_directory, windowMs: 1, ticks: () => clock += 10);
        log.Write("INFO", "第一筆");
        Directory.Delete(_directory, recursive: true);
        log.Write("INFO", "資料夾被刪掉時這筆可能遺失"); // 寫入失敗後會重新建立資料夾
        log.Write("INFO", "之後的紀錄");

        Assert.Contains(Files(), file => File.ReadAllText(file).Contains("之後的紀錄"));
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
