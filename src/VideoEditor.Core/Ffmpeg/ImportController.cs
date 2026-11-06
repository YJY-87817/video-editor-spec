using System.Text.Json;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>匯入流程對畫面的需求。由視窗實作，讓匯入流程不必知道任何控制項，也方便測試。</summary>
public interface IImportView
{
    /// <summary>這個檔案是否已經在素材清單裡（已匯入的不再重複匯入）。</summary>
    bool IsAlreadyImported(string path);

    /// <summary>更新讀取進度：說明文字與整體進度（0–1）。</summary>
    void ShowProgress(string text, double value);

    /// <summary>全部匯入完成，收起進度。</summary>
    void HideProgress();

    /// <summary>讀取成功，加入素材清單。沒有畫面或縮圖產生失敗時 thumbnail 為 null。</summary>
    void AddMedia(MediaInfo info, byte[]? thumbnail);

    /// <summary>這個檔案無法匯入。</summary>
    void ShowFailure(string name, string message);
}

/// <summary>逐一讀取素材資訊與縮圖，並把進度與結果交給 <see cref="IImportView"/>。</summary>
public sealed class ImportController(MediaProbe probe, FrameExtractor extractor, IImportView view, int thumbnailWidth = 144)
{
    private int _total;
    private int _done;
    // 已排入但還沒處理完的檔案：還不在素材清單裡，但也不能再排一次。
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    public async Task ImportAsync(IEnumerable<string> paths)
    {
        var pending = paths.Where(path => !view.IsAlreadyImported(path) && _queued.Add(path)).ToList();
        if (pending.Count == 0) return;
        // 匯入中又拖進新檔案時，併入同一個進度。
        _total += pending.Count;
        foreach (var path in pending)
        {
            var name = Path.GetFileName(path);
            try
            {
                Report(name, "讀取資訊", 0);
                var info = await probe.ProbeAsync(path);
                Report(name, "產生縮圖", 0.5);
                view.AddMedia(info, await LoadThumbnailAsync(info));
            }
            catch (Exception e) when (e is FfmpegException or InvalidDataException or JsonException)
            {
                view.ShowFailure(name, (e as FfmpegException)?.Stderr ?? e.Message);
            }
            finally
            {
                _queued.Remove(path);
                _done++;
                if (_done >= _total)
                {
                    _done = 0;
                    _total = 0;
                }
                Report(name, "完成", 0);
            }
        }
    }

    /// <summary>stage 為目前檔案內的進度（0–1）。全部完成時隱藏。</summary>
    private void Report(string name, string step, double stage)
    {
        if (_total == 0)
        {
            view.HideProgress();
            return;
        }
        view.ShowProgress($"讀取素材 {_done + 1}/{_total}：{name}（{step}）", (_done + stage) / _total);
    }

    private async Task<byte[]?> LoadThumbnailAsync(MediaInfo info)
    {
        if (!info.HasVideo) return null;
        try
        {
            var bytes = await extractor.ThumbnailAsync(info.Path, Math.Min(1, info.Duration / 10), thumbnailWidth);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (FfmpegException)
        {
            // 沒有縮圖不影響其他功能。
            return null;
        }
    }
}
