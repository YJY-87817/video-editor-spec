using System.Text.RegularExpressions;

namespace VideoEditor.Tests;

/// <summary>
/// 鎖住 Core 的命名空間依賴方向，避免循環相依長回來（A-2）。
/// 方向：Models ← Timeline ← Ffmpeg ← Export；Playback 不依賴其他資料夾；Logging 是最底層，誰都可以用，它自己不依賴任何資料夾。
/// 以原始碼文字檢查 using 與命名空間限定名稱，不需要建置額外工具。
/// </summary>
public class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Models"] = [],
        ["Timeline"] = ["Models"],
        ["Playback"] = [],
        ["Ffmpeg"] = ["Models", "Timeline"],
        ["Export"] = ["Models", "Timeline", "Ffmpeg"],
        ["Logging"] = [],
    };

    /// <summary>所有資料夾都可以使用的最底層。</summary>
    private const string Base = "Logging";

    private static string CoreDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VideoEditor.slnx")))
                return Path.Combine(directory.FullName, "src", "VideoEditor.Core");
        }
        throw new InvalidOperationException("找不到 VideoEditor.slnx，無法檢查原始碼。");
    }

    [Fact]
    public void CoreFoldersOnlyDependOnLowerLayers()
    {
        var violations = new List<string>();
        foreach (var (folder, allowed) in Allowed)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(CoreDirectory(), folder), "*.cs"))
            {
                var text = File.ReadAllText(file);
                foreach (var other in Allowed.Keys.Where(name => name != folder && name != Base && !allowed.Contains(name)))
                {
                    // using VideoEditor.Core.Export; 或 VideoEditor.Core.Export.X，以及 Export.ReverseGraph 這類限定名稱。
                    if (Regex.IsMatch(text, $@"VideoEditor\.Core\.{other}\b") || Regex.IsMatch(text, $@"(?<![\w.]){other}\.[A-Z]\w+"))
                        violations.Add($"{folder}/{Path.GetFileName(file)} 不應依賴 {other}");
                }
            }
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void EveryCoreFolderIsCovered()
    {
        // 新增資料夾時要決定它的位置，不能靠漏檢查蒙混過去。
        var folders = Directory.GetDirectories(CoreDirectory()).Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj")).ToHashSet();
        Assert.Subset(folders!, Allowed.Keys.ToHashSet());
        Assert.Subset(Allowed.Keys.ToHashSet(), folders!);
    }
}
