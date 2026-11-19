using System.Text.RegularExpressions;

namespace VideoEditor.Tests;

/// <summary>
/// 鎖住 Core 的命名空間依賴方向，避免循環相依長回來（A-2）。
/// 方向：Models ← Timeline ← Ffmpeg ← Export；Playback 不依賴其他資料夾；Logging 是最底層，誰都可以用，它自己不依賴任何資料夾。
/// 以原始碼文字檢查 using 與命名空間限定名稱，不需要建置額外工具；比對前先去掉注解與字串，
/// 文件注解裡提到另一個資料夾的類別（例如 see cref）不算依賴。
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

    /// <summary>
    /// 去掉 // 與 /* */ 注解、一般字串、逐字字串（@"…"）與字元常值，其他原樣保留。
    /// 不處理 C# 11 的原始字串（"""…"""）與插補字串裡的運算式：Core 的程式碼沒有用在依賴判斷上會出錯的寫法。
    /// </summary>
    /// <summary>位置 i 是字串的開頭（可有 $、@ 前綴）時，回傳開頭引號的位置與是否為逐字字串。</summary>
    private static (int QuoteAt, bool Verbatim)? StringStart(string source, int i)
    {
        var verbatim = false;
        var at = i;
        while (at < source.Length && source[at] is '$' or '@' && at - i < 2)
        {
            verbatim |= source[at] == '@';
            at++;
        }
        return at < source.Length && source[at] == '"' ? (at, verbatim) : null;
    }

    internal static string StripCommentsAndStrings(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                result.Append('\n');
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
            }
            else if (StringStart(source, i) is { } start)
            {
                // start.Verbatim：@"…"（"" 代表一個引號）；否則 \ 跳脫下一個字元。
                for (i = start.QuoteAt + 1; i < source.Length; i++)
                {
                    if (start.Verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { i++; continue; }
                    if (!start.Verbatim && source[i] == '\\') { i++; continue; }
                    if (source[i] == '"') break;
                }
                result.Append("\"\"");
            }
            else if (c == '\'' && next != '\0')
            {
                i++;
                if (source[i] == '\\') i++;
                while (i < source.Length && source[i] != '\'') i++;
                result.Append("''");
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }

    [Theory]
    [InlineData("var x = 1; // see Export.ReverseGraph", "Export.ReverseGraph")]
    [InlineData("/// <see cref=\"Export.ReverseGraph\"/>", "Export.ReverseGraph")]
    [InlineData("/* Export.ReverseGraph */ var x = 1;", "Export.ReverseGraph")]
    [InlineData("var s = \"Export.ReverseGraph\";", "Export.ReverseGraph")]
    [InlineData("var s = \"say \\\"Export.ReverseGraph\\\" \";", "Export.ReverseGraph")]
    [InlineData("var s = @\"C:\\Export.ReverseGraph\";", "Export.ReverseGraph")]
    [InlineData("var s = $\"{a} Export.ReverseGraph\";", "Export.ReverseGraph")]
    public void CommentsAndStringsAreIgnored(string source, string mention) =>
        Assert.DoesNotContain(mention, StripCommentsAndStrings(source));

    [Fact]
    public void RealCodeIsKept() =>
        Assert.Contains("Export.ReverseGraph.Plan(x)", StripCommentsAndStrings("var job = Export.ReverseGraph.Plan(x); // 注解"));

    [Fact]
    public void CodeAfterAStringIsKept() =>
        Assert.Contains("Export.ReverseGraph.Plan", StripCommentsAndStrings("var s = \"a // not a comment\"; var j = Export.ReverseGraph.Plan(s);"));

    [Fact]
    public void CoreFoldersOnlyDependOnLowerLayers()
    {
        var violations = new List<string>();
        foreach (var (folder, allowed) in Allowed)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(CoreDirectory(), folder), "*.cs"))
            {
                var text = StripCommentsAndStrings(File.ReadAllText(file));
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
