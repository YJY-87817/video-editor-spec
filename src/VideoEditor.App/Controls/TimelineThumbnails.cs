using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.App.Controls;

/// <summary>
/// 時間軸影片片段上的縮圖（UIUX-PLAN-2 #6）。<see cref="Get"/> 在繪製時呼叫：有快取就回傳，沒有就排進背景產生、先回傳 null。
/// 一次只跑一個 FFmpeg；最近一次繪製要的優先，已經捲出畫面（連續兩次繪製都沒再要）的請求直接丟掉。
/// 縮圖產生好之後，100 毫秒內完成的合併成一次重畫，播放時才不會一直多畫。所有成員只在 UI 執行緒使用。
/// </summary>
public sealed class TimelineThumbnails(FrameExtractor extractor, Action changed) : IDisposable
{
    /// <summary>產生的縮圖寬度（像素）。比畫上去的寬一些，高 DPI 螢幕上才不糊。</summary>
    private const int PixelWidth = 160;
    private const int CacheSize = 300;

    private readonly LruCache<(string Path, long Millis), BitmapSource?> _cache = new(CacheSize);
    private readonly Dictionary<(string Path, long Millis), (MediaInfo Media, double Time, long Generation)> _pending = [];
    private readonly CancellationTokenSource _cancel = new();
    private DispatcherTimer? _changedTimer;
    private long _generation;
    private bool _running;
    // 正在產生的那一張：產生期間時間軸還會一直重畫，不能再排一次。
    private (string Path, long Millis)? _inFlight;

    /// <summary>每次繪製時間軸前呼叫，用來判斷哪些請求已經過時。</summary>
    public void BeginRender() => _generation++;

    public BitmapSource? Get(MediaInfo media, double sourceTime)
    {
        var key = (media.Path, (long)Math.Round(sourceTime * 1000));
        if (_cache.TryGet(key, out var bitmap)) return bitmap; // 產生失敗的也記成 null，不會一直重試
        if (_inFlight == key) return null;
        _pending[key] = (media, sourceTime, _generation);
        Pump();
        return null;
    }

    private async void Pump()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (TakeNext() is { } next)
            {
                BitmapSource? bitmap = null;
                _inFlight = next.Key;
                try
                {
                    var bytes = await extractor.ThumbnailAsync(next.Media.Path, next.Time, PixelWidth, _cancel.Token, next.Media.IsStill);
                    if (bytes.Length > 0) bitmap = MainWindow.TryCreateThumbnail(bytes);
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e) when (ExpectedErrors.IsProcess(e))
                {
                    // 沒有縮圖不影響其他功能；記成 null，畫面上只顯示底色。
                }
                _inFlight = null;
                _cache.Add(next.Key, bitmap);
                ScheduleChanged();
            }
        }
        finally
        {
            _running = false;
            _inFlight = null;
        }
    }

    /// <summary>挑最近一次繪製要的請求；兩次繪製以前的（已經看不到）丟掉。</summary>
    private ((string Path, long Millis) Key, MediaInfo Media, double Time)? TakeNext()
    {
        foreach (var stale in _pending.Where(p => p.Value.Generation < _generation - 1).Select(p => p.Key).ToList())
            _pending.Remove(stale);
        if (_pending.Count == 0) return null;
        var next = _pending.MaxBy(p => p.Value.Generation);
        _pending.Remove(next.Key);
        return (next.Key, next.Value.Media, next.Value.Time);
    }

    private void ScheduleChanged()
    {
        if (_changedTimer != null) return;
        _changedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _changedTimer.Tick += (_, _) =>
        {
            _changedTimer?.Stop();
            _changedTimer = null;
            changed();
        };
        _changedTimer.Start();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _changedTimer?.Stop();
        _cancel.Dispose();
    }
}
