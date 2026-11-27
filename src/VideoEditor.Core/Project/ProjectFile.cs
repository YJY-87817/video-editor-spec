using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Project;

/// <summary>
/// 專案檔（.vedit，UIUX-PLAN-2 決定 2）：素材清單、兩軌的片段、畫面比例與處理方式。
/// 不存復原紀錄與預覽位置。素材只存路徑，開啟時重新讀取素材資訊（檔案可能換過）。
/// </summary>
public sealed record ProjectFile
{
    public const int CurrentVersion = 1;
    public const string Extension = ".vedit";

    public int Version { get; init; } = CurrentVersion;
    public List<ProjectMedia> Media { get; init; } = [];
    public List<ProjectClip> Video { get; init; } = [];
    public List<ProjectClip> Music { get; init; } = [];
    /// <summary>{"W":16,"H":9}；原尺寸是 {"W":0,"H":0}。</summary>
    public AspectRatio Ratio { get; init; } = AspectRatio.Original;
    public FitMode Mode { get; init; } = FitMode.Fill;
    /// <summary>只用於自動存檔：這份剪輯原本對應的專案檔（還原後存檔時存回這裡）。</summary>
    public string? SourceProjectPath { get; init; }
}

/// <summary>素材的位置：絕對路徑，以及相對於專案檔資料夾的路徑（整個資料夾搬家後還找得到；不同磁碟時為 null）。</summary>
public sealed record ProjectMedia(string Path, string? RelativePath);

/// <summary>一段片段。<see cref="Media"/> 是 <see cref="ProjectFile.Media"/> 的索引。</summary>
public sealed record ProjectClip(int Media, double SourceIn, double SourceOut, double Start, double Volume = 1, bool Reversed = false,
    int Rotation = 0, bool FlipHorizontal = false, bool FlipVertical = false, double Zoom = 1, double CenterX = 0.5, double CenterY = 0.5);

/// <summary>讀取專案檔的結果：成功時 <see cref="File"/> 有值，失敗時 <see cref="Error"/> 是給人看的說明。</summary>
public sealed record ProjectLoadResult(ProjectFile? File, string? Error);

/// <summary>專案檔與時間軸之間的轉換、存檔與讀檔。讀檔不丟出例外，內容不合理時回傳說明。</summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 由目前的狀態建立專案檔。素材清單依 <paramref name="media"/> 的順序，時間軸上用到、但不在清單裡的素材接在後面。
    /// </summary>
    public static ProjectFile Create(Timeline timeline, IEnumerable<MediaInfo> media, AspectRatio ratio, FitMode mode, string projectPath,
        string? sourceProjectPath = null)
    {
        var list = media.Concat(timeline.AllClips.Select(c => c.Media)).Distinct().ToList();
        var index = list.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath)) ?? "";
        ProjectClip ToClip(TimelineClip c) => new(index[c.Media], c.SourceIn, c.SourceOut, c.Start, c.Volume, c.Reversed,
            c.Transform.Rotation, c.Transform.FlipHorizontal, c.Transform.FlipVertical, c.Framing.Zoom, c.Framing.CenterX, c.Framing.CenterY);
        return new ProjectFile
        {
            Media = [.. list.Select(m => new ProjectMedia(m.Path, Relative(folder, m.Path)))],
            Video = [.. timeline.VideoClips.Select(ToClip)],
            Music = [.. timeline.MusicClips.Select(ToClip)],
            Ratio = ratio,
            Mode = mode,
            SourceProjectPath = sourceProjectPath,
        };
    }

    private static string? Relative(string folder, string path)
    {
        if (folder.Length == 0 || !System.IO.Path.IsPathFullyQualified(path)) return null;
        var relative = System.IO.Path.GetRelativePath(folder, path);
        // 不同磁碟時 GetRelativePath 會原樣傳回完整路徑，那就沒有相對路徑可言。
        return System.IO.Path.IsPathFullyQualified(relative) ? null : relative;
    }

    /// <summary>寫入專案檔：先寫暫存檔再取代，寫到一半失敗也不會弄壞原本的檔案。失敗時丟出 IOException 等例外，由呼叫端顯示。</summary>
    public static void Save(ProjectFile project, string path)
    {
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static ProjectLoadResult Load(string path)
    {
        try
        {
            var project = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Options);
            if (project == null) return Fail("專案檔是空的。");
            return Validate(project) is { } problem ? Fail(problem) : new ProjectLoadResult(project, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"讀不到專案檔：{e.Message}");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return Fail($"專案檔的格式不對，可能已經損壞：{e.Message}");
        }
    }

    private static ProjectLoadResult Fail(string message) => new(null, message);

    /// <summary>檢查內容是否合理；有問題時回傳說明。</summary>
    public static string? Validate(ProjectFile project)
    {
        if (project.Version > ProjectFile.CurrentVersion) return $"這個專案檔是較新版本的程式存的（版本 {project.Version}），請更新程式後再開啟。";
        if (project.Version < 1) return "專案檔的版本號不對。";
        if (project.Media == null || project.Video == null || project.Music == null) return "專案檔缺少必要的內容。";
        if (project.Media.Any(m => m == null || string.IsNullOrWhiteSpace(m.Path))) return "專案檔裡有素材沒有路徑。";
        if (project.Ratio == null || !(project.Ratio.IsOriginal || AspectRatio.Presets.Contains(project.Ratio))) return "專案檔的畫面比例不是程式提供的比例。";
        if (!Enum.IsDefined(project.Mode)) return "專案檔的處理方式不對。";
        foreach (var clip in project.Video.Concat(project.Music))
        {
            if (clip == null) return "專案檔裡有空的片段。";
            if (clip.Media < 0 || clip.Media >= project.Media.Count) return "專案檔裡有片段指到不存在的素材。";
            if (!double.IsFinite(clip.SourceIn) || !double.IsFinite(clip.SourceOut) || !double.IsFinite(clip.Start)
                || clip.SourceIn < 0 || clip.Start < 0 || clip.SourceOut - clip.SourceIn < Timeline.MinClipDuration - 1e-6)
                return "專案檔裡有片段的時間不對。";
            if (!double.IsFinite(clip.Volume) || clip.Volume < 0 || clip.Volume > VolumeCurve.MaxGain) return "專案檔裡有片段的音量不對。";
            if (clip.Rotation is not (0 or 90 or 180 or 270)) return "專案檔裡有片段的旋轉角度不對。";
            if (!double.IsFinite(clip.Zoom) || !double.IsFinite(clip.CenterX) || !double.IsFinite(clip.CenterY)) return "專案檔裡有片段的畫面位置不對。";
        }
        return null;
    }

    /// <summary>
    /// 找素材的實際位置：先用絕對路徑，找不到再用相對於（目前）專案檔資料夾的路徑。都找不到時回傳 null。
    /// </summary>
    public static string? ResolveMediaPath(ProjectMedia media, string projectPath, Func<string, bool> exists)
    {
        if (exists(media.Path)) return media.Path;
        if (media.RelativePath is { Length: > 0 } relative && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath)) is { } folder)
        {
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative));
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 依專案檔重建時間軸（整個換掉）。<paramref name="media"/> 和 <see cref="ProjectFile.Media"/> 一一對應，找不到或略過的是 null。
    /// 素材找不到、素材比片段短、或和前一段重疊的片段略過，回傳說明。
    /// </summary>
    public static IReadOnlyList<string> Build(ProjectFile project, IReadOnlyList<MediaInfo?> media, Timeline timeline)
    {
        var problems = new List<string>();
        List<ClipState> Track(IEnumerable<ProjectClip> clips, TrackKind kind)
        {
            var states = new List<ClipState>();
            foreach (var clip in clips.OrderBy(c => c.Start))
            {
                var info = media[clip.Media];
                var name = System.IO.Path.GetFileName(project.Media[clip.Media].Path);
                if (info == null)
                {
                    problems.Add($"{name}：找不到素材，略過這段片段。");
                    continue;
                }
                if (kind == TrackKind.Video && !info.HasVideo || kind == TrackKind.Music && !info.HasAudio)
                {
                    problems.Add($"{name}：素材已經沒有{(kind == TrackKind.Video ? "畫面" : "聲音")}，略過這段片段。");
                    continue;
                }
                var (sourceIn, sourceOut) = info.IsStill ? (0, Math.Min(clip.SourceOut - clip.SourceIn, Timeline.MaxStillDuration)) : (clip.SourceIn, clip.SourceOut);
                if (!info.IsStill && sourceOut > info.Duration + 1e-3)
                {
                    problems.Add($"{name}：素材比專案裡用到的範圍短（可能換過檔案），略過這段片段。");
                    continue;
                }
                if (states.Count > 0 && clip.Start < states[^1].Start + (states[^1].SourceOut - states[^1].SourceIn) - 1e-6)
                {
                    problems.Add($"{name}：和前一段片段重疊，略過這段片段。");
                    continue;
                }
                var timelineClip = new TimelineClip(info, kind, sourceIn, sourceOut, clip.Start);
                var video = kind == TrackKind.Video;
                states.Add(new ClipState(timelineClip, sourceIn, sourceOut, clip.Start, clip.Volume,
                    video && clip.Reversed && !info.IsStill,
                    video ? new FrameTransform(clip.Rotation, clip.FlipHorizontal, clip.FlipVertical) : default,
                    video ? new ClipFraming(clip.Zoom, clip.CenterX, clip.CenterY) : ClipFraming.Default));
            }
            return states;
        }
        timeline.Restore(new TimelineSnapshot(Track(project.Video, TrackKind.Video), Track(project.Music, TrackKind.Music)));
        return problems;
    }
}

/// <summary>
/// 判斷有沒有沒存的變更（決定 5）：時間軸、畫面比例、處理方式與素材清單都和存檔時一樣，就是沒有變更。
/// </summary>
public sealed class ProjectState(TimelineSnapshot timeline, AspectRatio ratio, FitMode mode, IReadOnlyList<string> mediaPaths) : IEquatable<ProjectState>
{
    public TimelineSnapshot Timeline { get; } = timeline;
    public AspectRatio Ratio { get; } = ratio;
    public FitMode Mode { get; } = mode;
    public IReadOnlyList<string> MediaPaths { get; } = mediaPaths;

    public bool Equals(ProjectState? other) =>
        other != null && Timeline.Equals(other.Timeline) && Ratio == other.Ratio && Mode == other.Mode && MediaPaths.SequenceEqual(other.MediaPaths);

    public override bool Equals(object? obj) => Equals(obj as ProjectState);
    public override int GetHashCode() => HashCode.Combine(MediaPaths.Count, Mode);
}

/// <summary>
/// 自動存檔的時機（決定 4）：狀態改變後維持 3 秒不變才存（拖曳、連續修改中不存），和上次自動存的一樣就不存。
/// 呼叫端每秒左右 <see cref="Observe"/> 一次。時間由 <see cref="TimeProvider"/> 注入，方便測試。
/// </summary>
public sealed class AutoSaveTracker(TimeProvider? time = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ProjectState? _observed;
    private DateTimeOffset _observedAt;
    private ProjectState? _saved;

    /// <summary>看一下目前的狀態；回傳 true 表示現在該自動存檔（存完要呼叫 <see cref="MarkSaved"/>）。</summary>
    public bool Observe(ProjectState current)
    {
        var now = _time.GetUtcNow();
        if (!current.Equals(_observed))
        {
            _observed = current;
            _observedAt = now;
            return false;
        }
        return !current.Equals(_saved) && now - _observedAt >= Quiet;
    }

    public void MarkSaved(ProjectState state) => _saved = state;

    /// <summary>手動存檔或開啟專案後：這個狀態已經安全，不必再自動存。</summary>
    public void Reset(ProjectState state)
    {
        _saved = state;
        _observed = state;
        _observedAt = _time.GetUtcNow();
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "autosave" + ProjectFile.Extension);
}
