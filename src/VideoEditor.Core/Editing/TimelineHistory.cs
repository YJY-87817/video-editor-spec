using VideoEditor.Core.Models;

namespace VideoEditor.Core.Editing;

/// <summary>一個片段在某個時間點的狀態。片段本身以參照記錄，還原時寫回同一個物件。</summary>
public sealed record ClipState(TimelineClip Clip, double SourceIn, double SourceOut, double Start, double Volume, bool Reversed,
    FrameTransform Transform, ClipFraming Framing)
{
    /// <summary>新增會影響輸出的片段屬性時，這裡、<see cref="ApplyTo"/> 與 <see cref="TimelineClip.CopySettingsTo"/> 都要加。</summary>
    internal static ClipState Of(TimelineClip clip) =>
        new(clip, clip.SourceIn, clip.SourceOut, clip.Start, clip.Volume, clip.Reversed, clip.Transform, clip.Framing);

    internal void ApplyTo()
    {
        Clip.SourceIn = SourceIn;
        Clip.SourceOut = SourceOut;
        Clip.Start = Start;
        Clip.Volume = Volume;
        Clip.Reversed = Reversed;
        Clip.Transform = Transform;
        Clip.Framing = Framing;
    }
}

/// <summary>整條時間軸在某個時間點的樣子：兩軌的片段、順序與狀態。</summary>
public sealed class TimelineSnapshot(IReadOnlyList<ClipState> video, IReadOnlyList<ClipState> music) : IEquatable<TimelineSnapshot>
{
    public IReadOnlyList<ClipState> Video { get; } = video;
    public IReadOnlyList<ClipState> Music { get; } = music;

    public bool Equals(TimelineSnapshot? other) =>
        other != null && Video.SequenceEqual(other.Video) && Music.SequenceEqual(other.Music);

    public override bool Equals(object? obj) => Equals(obj as TimelineSnapshot);

    public override int GetHashCode() => HashCode.Combine(Video.Count, Music.Count);
}

/// <summary>
/// 時間軸的復原與重做。用法：修改前 <see cref="Capture"/>，改完 <see cref="Commit"/>；
/// 沒有真的改變（例如點了片段沒拖動、分割失敗）時 Commit 不會留下紀錄，所以呼叫端不必先判斷操作有沒有成功。
/// 只記錄時間軸，不記錄素材清單、畫面比例這類輸出設定。所有成員只在 UI 執行緒使用。
/// </summary>
public sealed class TimelineHistory(Timeline timeline, int capacity = TimelineHistory.DefaultCapacity, TimeProvider? time = null)
{
    public const int DefaultCapacity = 100;

    /// <summary>同一個合併鍵的修改，間隔在這個時間以內就算同一步（例如拖曳音量滑桿、連續滾動滾輪）。</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1);

    private sealed record Entry(TimelineSnapshot Snapshot, string Label, string? MergeKey, DateTimeOffset Time);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // 最新的一步在最後。超過上限時丟掉最舊的。
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];

    /// <summary>可以復原或重做的狀態有變化時觸發（給按鈕更新可不可以按、提示文字）。</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>下一次復原會復原的操作名稱；不能復原時為 null。</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>下一次重做會重做的操作名稱；不能重做時為 null。</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public TimelineSnapshot Capture() => timeline.Capture();

    /// <summary>
    /// 把修改前的樣子記成一步。時間軸和 <paramref name="before"/> 一樣時不記錄，回傳 false。
    /// <paramref name="mergeKey"/> 和上一步相同、而且在 <see cref="MergeWindow"/> 以內時，併入上一步（保留上一步修改前的樣子）；
    /// 併完後如果剛好回到原樣，上一步也一起拿掉。
    /// </summary>
    public bool Commit(TimelineSnapshot before, string label, string? mergeKey = null)
    {
        var now = _time.GetUtcNow();
        if (mergeKey != null && _redo.Count == 0 && _undo.Count > 0 && _undo[^1] is var last
            && last.MergeKey == mergeKey && now - last.Time <= MergeWindow)
        {
            if (last.Snapshot.Equals(timeline.Capture())) _undo.RemoveAt(_undo.Count - 1);
            else _undo[^1] = last with { Time = now };
            Changed?.Invoke();
            return true;
        }

        if (before.Equals(timeline.Capture())) return false;
        _undo.Add(new Entry(before, label, mergeKey, now));
        if (_undo.Count > capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
        return true;
    }

    /// <summary>復原上一步，回傳復原的操作名稱；沒有可以復原的時回傳 null。</summary>
    public string? Undo() => Step(_undo, _redo);

    /// <summary>重做剛才復原的一步，回傳操作名稱；沒有可以重做的時回傳 null。</summary>
    public string? Redo() => Step(_redo, _undo);

    private string? Step(List<Entry> from, List<Entry> to)
    {
        if (from.Count == 0) return null;
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        // 反方向的那一步記下現在的樣子；不帶合併鍵，復原後的新修改不會併進去。
        to.Add(new Entry(timeline.Capture(), entry.Label, null, _time.GetUtcNow()));
        timeline.Restore(entry.Snapshot);
        Changed?.Invoke();
        return entry.Label;
    }
}
