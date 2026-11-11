using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Playback;
using VideoEditor.Core.Timeline;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App.Preview;

/// <summary>
/// 預覽引擎。播放時由 LibVLC 解碼（影片與配樂各一個播放器），
/// 暫停與拖曳時由 FFmpeg 解出精確影格，因為 LibVLC 無法倒退逐格。
/// 倒放片段改播 FFmpeg 預先產生的倒放預覽檔；倒帶（往回播放）則由 FFmpeg 成批解出影格顯示，沒有聲音。
/// 除了 LibVLC 回呼之外，所有成員都只在 UI 執行緒使用。
/// </summary>
public sealed class PreviewEngine : IDisposable
{
    private const int MaxPreviewLongSide = 1280;
    private const int FrameCacheCapacity = 64;
    private const double DefaultFrameRate = 30;
    private const long FirstFrameTimeoutMs = 1500;
    private const double VideoDriftLimitMs = 500;
    private const double MusicDriftLimitMs = 300;
    private const int ReverseProxyLongSide = 854;
    private const int RewindBatchFrames = 12;
    private const int MaxRewindSpeed = 8;
    private const int MaxPlaybackRate = 8;
    /// <summary>逐格移動時一次解出的格數。</summary>
    private const int StepBatchFrames = 12;
    /// <summary>逐格移動時，前方這幾格內有還沒解出的，就先解下一批。</summary>
    private const int StepPrefetchLead = 4;
    /// <summary>這個倍速以上靜音：LibVLC 高倍速的聲音斷斷續續，聽起來只是雜音。</summary>
    private const int MuteFromRate = 4;

    private readonly FrameExtractor _extractor;
    private readonly ReversePreviewCache _proxies;
    private readonly Dispatcher _dispatcher;
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _videoPlayer;
    private readonly MediaPlayer _musicPlayer;
    private readonly VlcVideoSink _sink = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _firstFrameWait = new();
    private readonly LruCache<(string Path, long Frame), RawFrame> _frames = new(FrameCacheCapacity);

    // LibVLC 的 Play/Stop 可能阻塞，全部排進單一背景佇列，依序執行。
    private Task _commands = Task.CompletedTask;

    private TimelineModel _timeline = new();
    private WriteableBitmap? _bitmap;
    private MediaInfo? _surfaceMedia;
    private FrameTransform _surfaceTransform;
    private double _clockBase;
    private TimelineClip? _videoClip;
    private TimelineClip? _musicClip;
    private bool _waitingForFirstFrame;
    private long _framesAtSwitch;
    private bool _acceptVlcFrames;
    /// <summary>
    /// 一個待解的預覽影格。Batch 不為 null 時一次解出整批（逐格移動用）；Show 為 false 表示只是預先解好放進快取。
    /// </summary>
    private readonly record struct FrameRequest(MediaInfo Media, FrameTransform Transform, Size2 Size, double Fps, long Frame, long Request,
        (long Start, int Count)? Batch = null, bool Show = true);

    private FrameRequest? _pendingFrame;
    // 正在背景預先解碼時可用來中止它，讓要立刻顯示的畫面先解。
    private CancellationTokenSource? _prefetchCancel;
    private long _latestRequest;
    private long _shownRequest;
    private bool _extracting;

    // 播放器時間 = 時間軸位置 - 片段起點 + _playerBase。一般片段為 SourceIn，倒放預覽檔為 0。
    private double _playerBase;
    private (TimelineClip Clip, Task<string> File, Size2 Size)? _proxyWait;

    private readonly DispatcherTimer _rewindTimer;
    private readonly Stopwatch _rewindClock = new();
    private double _rewindBase;
    private bool _rewindFetching;
    // 來源結尾附近解不出的影格；倒帶與逐格預先解碼都不再重試。
    private readonly HashSet<(string Path, long Frame)> _missingFrames = [];

    public PreviewEngine(FrameExtractor extractor, ReversePreviewCache proxies, Dispatcher dispatcher)
    {
        _extractor = extractor;
        _proxies = proxies;
        _dispatcher = dispatcher;
        _vlc = new LibVLC("--no-video-title-show", "--quiet");
        _videoPlayer = new MediaPlayer(_vlc);
        _musicPlayer = new MediaPlayer(_vlc);
        _sink.Attach(_videoPlayer);
        _sink.FrameAvailable = () => _dispatcher.BeginInvoke(PresentVlcFrame, DispatcherPriority.Render);
        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _rewindTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _rewindTimer.Tick += (_, _) => RewindTick();
    }

    public double Position { get; private set; }
    public bool IsPlaying { get; private set; }
    /// <summary>倒帶速度（1、2、4、8 倍）；0 表示沒有在倒帶。</summary>
    public int RewindSpeed { get; private set; }
    /// <summary>播放倍速（1、2、4、8）；快轉時大於 1。</summary>
    public int PlaybackRate { get; private set; } = 1;
    public bool IsFastForwarding => IsPlaying && PlaybackRate > 1;
    public bool IsRewinding => RewindSpeed > 0;
    public double Duration => _timeline.Duration;

    public event Action? PositionChanged;
    public event Action? PlayingChanged;
    /// <summary>
    /// 顯示的素材、畫面尺寸或旋轉鏡像改變。bitmap 為 null 表示時間軸空白處，顯示黑畫面。
    /// bitmap 是原始方向的畫面，旋轉與鏡像由畫面元件套用。
    /// </summary>
    public event Action<WriteableBitmap?, MediaInfo?, FrameTransform>? SurfaceChanged;
    public event Action<string>? Error;
    public event Action<string>? Status;

    public void Load(TimelineModel timeline, double position = 0)
    {
        Stop();
        _timeline = timeline;
        Seek(position);
    }

    public void Play()
    {
        StopRewind();
        if (IsPlaying || Duration <= 0) return;
        if (Position >= Duration - 0.01) Position = 0;
        IsPlaying = true;
        _acceptVlcFrames = true;
        // 保險：清掉可能殘留的「影格待處理」狀態，確保新影格會通知。
        _sink.ResetPending();
        _clockBase = Position;
        _videoClip = null;
        _musicClip = null;
        _waitingForFirstFrame = false;
        _clock.Restart();
        _timer.Start();
        PlayingChanged?.Invoke();
        Tick();
    }

    /// <summary>快轉：沒在播放時以 2 倍速開始，播放中再按一次加倍（最多 8 倍）。</summary>
    public void FastForward()
    {
        if (Duration <= 0) return;
        if (!IsPlaying) Play();
        if (!IsPlaying) return;
        SetRate(Math.Min(MaxPlaybackRate, PlaybackRate * 2));
    }

    /// <summary>J/K/L 的 L：沒在播放時以正常速度播放，播放中則加速。</summary>
    public void PlayOrFaster()
    {
        if (IsPlaying) FastForward();
        else Play();
    }

    private void SetRate(int rate)
    {
        if (rate == PlaybackRate) return;
        // 從目前位置以新倍速重新起算；等待第一格時時鐘本來就停著，不用動。
        if (!_waitingForFirstFrame)
        {
            _clockBase = Position;
            _clock.Restart();
        }
        PlaybackRate = rate;
        Enqueue(() =>
        {
            _videoPlayer.SetRate(rate);
            _musicPlayer.SetRate(rate);
        });
        PlayingChanged?.Invoke();
    }

    public void Pause()
    {
        if (!IsPlaying && !IsRewinding) return;
        Stop();
        ShowFrameAtPosition();
    }

    public void Toggle()
    {
        if (IsPlaying || IsRewinding) Pause();
        else Play();
    }

    /// <summary>倒帶：播放頭往回走。倒帶中再按一次加倍速度（最多 8 倍）。</summary>
    public void Rewind()
    {
        if (Duration <= 0) return;
        if (IsRewinding)
        {
            // 從目前位置以新速度重新起算。
            _rewindBase = Position;
            _rewindClock.Restart();
            RewindSpeed = Math.Min(MaxRewindSpeed, RewindSpeed * 2);
            PlayingChanged?.Invoke();
            return;
        }

        Stop();
        if (Position <= 0.01) Position = Duration;
        RewindSpeed = 1;
        _rewindBase = Position;
        _rewindClock.Restart();
        _rewindTimer.Start();
        PlayingChanged?.Invoke();
        RewindTick();
    }

    /// <summary>
    /// 時間軸編輯後呼叫：預先產生每個倒放片段的預覽檔，播放到該片段時就不必等待；
    /// 範圍已經改掉（修剪、刪除、取消倒放）的舊預覽檔則停止產生並刪除。
    /// </summary>
    public void SyncReverse(TimelineModel timeline) =>
        _proxies.Sync(timeline.VideoClips.Where(clip => clip.Reversed).Select(clip => new ReversePreviewRequest(
            clip.Media, clip.SourceIn, clip.SourceOut,
            FrameExtractor.PreviewSize(clip.Media.DisplayWidth, clip.Media.DisplayHeight, ReverseProxyLongSide))).ToList());

    /// <summary>移動播放頭。播放中拖曳會先暫停。</summary>
    public void Seek(double time) => SeekTo(time, 0);

    /// <summary>sourceStep 不為 0 表示是逐格移動，值為來源影格的移動方向，用來一次多解幾格。</summary>
    private void SeekTo(double time, int sourceStep)
    {
        Stop();
        Position = Math.Clamp(time, 0, Duration);
        PositionChanged?.Invoke();
        ShowFrameAtPosition(sourceStep);
    }

    public void StepFrame(int direction)
    {
        Stop();
        var clip = _timeline.VideoClipAt(Position);
        var fps = clip != null && clip.Media.FrameRate.Value > 0 ? clip.Media.FrameRate.Value : DefaultFrameRate;
        if (clip == null)
        {
            Seek(Position + direction / fps);
            return;
        }
        // 倒放片段在時間軸上往後一格，是來源的前一格。
        var sourceStep = clip.Reversed ? -direction : direction;
        SeekTo(clip.ToTimelineTime((FrameExtractor.FrameIndex(clip.ToSourceTime(Position), fps) + sourceStep) / fps), sourceStep);
    }

    /// <summary>時間軸內容改變（拖曳、修剪、刪除）後，重畫目前位置的影格。</summary>
    /// <remarks>播放中（例如播放時把片段加入時間軸）會從同一位置繼續播放，而不是停下來。</remarks>
    public void Refresh()
    {
        var wasPlaying = IsPlaying;
        var rate = PlaybackRate;
        Seek(Math.Min(Position, Duration));
        if (!wasPlaying) return;
        Play();
        SetRate(rate);
    }

    private void Stop()
    {
        StopRewind();
        if (!IsPlaying) return;
        IsPlaying = false;
        _acceptVlcFrames = false;
        _timer.Stop();
        _clock.Stop();
        Enqueue(() =>
        {
            _videoPlayer.Stop();
            _musicPlayer.Stop();
        });
        _videoClip = null;
        _musicClip = null;
        _proxyWait = null;
        PlaybackRate = 1;
        PlayingChanged?.Invoke();
    }

    private void StopRewind()
    {
        if (!IsRewinding) return;
        RewindSpeed = 0;
        _rewindTimer.Stop();
        _rewindClock.Stop();
        PlayingChanged?.Invoke();
    }

    private void Tick()
    {
        if (!IsPlaying) return;
        if (_proxyWait is { } wait)
        {
            // 倒放預覽檔還在產生：時鐘停在片段起點，畫面維持不動。
            if (!wait.File.IsCompleted) return;
            _proxyWait = null;
            Enqueue(() => _musicPlayer.SetPause(false));
            if (wait.File.IsCanceled)
            {
                // 等待期間片段被修剪或刪除，這個預覽檔已經不需要了，不是錯誤。
                Status?.Invoke("");
                Pause();
                return;
            }
            if (!wait.File.IsCompletedSuccessfully)
            {
                var reason = wait.File.Exception?.InnerException is FfmpegException f ? f.Stderr : wait.File.Exception?.InnerException?.Message;
                Error?.Invoke($"無法產生倒放預覽：{reason}");
                Pause();
                return;
            }
            Status?.Invoke("");
            StartVideo(wait.Clip, wait.File.Result, _clockBase, 0, wait.Size);
        }

        if (_waitingForFirstFrame
            && (_sink.FramesDisplayed > _framesAtSwitch || _firstFrameWait.ElapsedMilliseconds > FirstFrameTimeoutMs))
        {
            // 新片段的第一格出現後，時鐘才繼續走，畫面和位置才不會錯開。
            _waitingForFirstFrame = false;
            _clock.Restart();
        }

        var position = _waitingForFirstFrame ? _clockBase : _clockBase + _clock.Elapsed.TotalSeconds * PlaybackRate;
        if (position >= Duration)
        {
            Stop();
            Position = Duration;
            PositionChanged?.Invoke();
            ShowFrameAtPosition();
            return;
        }

        UpdateVideo(position);
        UpdateMusic(position);
        if (_waitingForFirstFrame) position = _clockBase;
        else position = FollowVideoClock(position);

        ApplyVolume(_videoPlayer, _videoClip, PlaybackRate);
        ApplyVolume(_musicPlayer, _musicClip, PlaybackRate);
        Position = position;
        PositionChanged?.Invoke();
    }

    private void UpdateVideo(double position)
    {
        var clip = _timeline.VideoClipAt(position);
        if (clip == _videoClip) return;
        _videoClip = clip;

        if (clip == null)
        {
            Enqueue(() => _videoPlayer.Stop());
            ShowBlack();
            return;
        }

        var size = FrameExtractor.PreviewSize(clip.Media.DisplayWidth, clip.Media.DisplayHeight, MaxPreviewLongSide);
        EnsureSurface(size, clip.Media, clip.Transform);
        _clockBase = position;
        if (!clip.Reversed)
        {
            StartVideo(clip, clip.Media.Path, position, clip.SourceIn, size);
            return;
        }

        var proxySize = FrameExtractor.PreviewSize(clip.Media.DisplayWidth, clip.Media.DisplayHeight, ReverseProxyLongSide);
        var file = _proxies.GetAsync(clip.Media, clip.SourceIn, clip.SourceOut, proxySize);
        if (file.IsCompletedSuccessfully)
        {
            StartVideo(clip, file.Result, position, 0, size);
            return;
        }
        // 預覽檔還沒好：先停下時鐘與配樂，等 Tick 發現完成後再開始播。
        Enqueue(() => _videoPlayer.Stop());
        Enqueue(() => _musicPlayer.SetPause(true));
        _waitingForFirstFrame = true;
        _proxyWait = (clip, file, size);
        Status?.Invoke("正在準備倒放預覽…");
    }

    /// <summary>從 position 開始播放片段。playerBase 是播放器時間與片段內位置的差（見 _playerBase）。</summary>
    private void StartVideo(TimelineClip clip, string path, double position, double playerBase, Size2 size)
    {
        _playerBase = playerBase;
        var start = position - clip.Start + playerBase;
        var stop = clip.Duration + playerBase;
        _clockBase = position;
        _framesAtSwitch = _sink.FramesDisplayed;
        _waitingForFirstFrame = true;
        _firstFrameWait.Restart();
        Enqueue(() =>
        {
            _sink.SetTarget(size);
            PlayMedia(_videoPlayer, path, start, stop);
        });
    }

    private void UpdateMusic(double position)
    {
        var clip = _timeline.MusicClipAt(position);
        if (clip == _musicClip)
        {
            if (clip == null || _waitingForFirstFrame) return;
            var expected = clip.ToSourceTime(position) * 1000;
            var actual = _musicPlayer.Time;
            if (actual > 0 && Math.Abs(actual - expected) > MusicDriftLimitMs * PlaybackRate)
                Enqueue(() => _musicPlayer.Time = (long)expected);
            return;
        }

        _musicClip = clip;
        if (clip == null)
        {
            Enqueue(() => _musicPlayer.Stop());
            return;
        }

        var sourceTime = clip.ToSourceTime(position);
        var sourceOut = clip.SourceOut;
        var path = clip.Media.Path;
        Enqueue(() => PlayMedia(_musicPlayer, path, sourceTime, sourceOut, ":no-video"));
    }

    /// <summary>若 LibVLC 實際進度與時鐘差太多（如解碼跟不上），以 LibVLC 為準，避免畫面與播放頭脫鉤。</summary>
    private double FollowVideoClock(double position)
    {
        if (_videoClip == null) return position;
        var actual = _videoPlayer.Time;
        if (actual <= 0) return position;
        var expected = (position - _videoClip.Start + _playerBase) * 1000;
        // 倍速時同樣的延遲會造成較大的來源時間差，容許值跟著放大。
        if (Math.Abs(actual - expected) <= VideoDriftLimitMs * PlaybackRate) return position;

        var corrected = _videoClip.Start + actual / 1000.0 - _playerBase;
        _clockBase = corrected;
        _clock.Restart();
        return corrected;
    }

    private static void ApplyVolume(MediaPlayer player, TimelineClip? clip, int rate)
    {
        if (clip == null) return;
        var volume = rate >= MuteFromRate ? 0 : VolumeCurve.ToVlcVolume(clip.Volume);
        if (player.Volume != volume) player.Volume = volume;
    }

    private void PlayMedia(MediaPlayer player, string path, double start, double stop, params string[] options)
    {
        using var media = new Media(_vlc, path);
        media.AddOption(":start-time=" + start.ToString(CultureInfo.InvariantCulture));
        media.AddOption(":stop-time=" + stop.ToString(CultureInfo.InvariantCulture));
        foreach (var option in options) media.AddOption(option);
        player.Play(media);
        // 新的媒體要重新套用倍速（在佇列上執行，讀到的是最新的倍速）。
        player.SetRate(PlaybackRate);
    }

    private void Enqueue(Action command)
    {
        _commands = _commands.ContinueWith(_ =>
        {
            try { command(); }
            catch (Exception e) when (e is VLCException or InvalidOperationException)
            {
                _dispatcher.BeginInvoke(() => Error?.Invoke($"播放失敗：{e.Message}"));
            }
        }, TaskScheduler.Default);
    }

    // ---- 倒帶 ----

    private void RewindTick()
    {
        if (!IsRewinding) return;
        var position = _rewindBase - _rewindClock.Elapsed.TotalSeconds * RewindSpeed;
        if (position <= 0)
        {
            StopRewind();
            Position = 0;
            PositionChanged?.Invoke();
            ShowFrameAtPosition();
            return;
        }
        Position = position;
        PositionChanged?.Invoke();
        ShowRewindFrame();
    }

    /// <summary>顯示目前位置附近已解出的影格，並預先成批解出接下來（往回）要用的影格。</summary>
    private void ShowRewindFrame()
    {
        var clip = _timeline.VideoClipAt(Position);
        if (clip == null)
        {
            ShowBlack();
            return;
        }

        var media = clip.Media;
        var fps = media.FrameRate.Value > 0 ? media.FrameRate.Value : DefaultFrameRate;
        var frame = FrameExtractor.FrameIndex(clip.ToSourceTime(Position), fps);
        // 時間軸往回走時，一般片段的來源時間變小，倒放片段的來源時間變大。
        var direction = clip.Reversed ? 1 : -1;
        var step = RewindSpeed;
        var (first, last) = FrameRange(clip, fps);

        // 高倍速只解每 step 格中的一格，所以找最近的一格來顯示。
        for (var d = 0; d < step; d++)
        {
            if (!_frames.TryGet((media.Path, frame - d * direction), out var cached)) continue;
            ShowFrame(cached, media, clip.Transform);
            break;
        }

        if (_rewindFetching) return;
        for (var i = 0; i < RewindBatchFrames; i++)
        {
            var target = frame + i * step * direction;
            if (target < first || target > last) return;
            if (_frames.TryGet((media.Path, target), out _) || _missingFrames.Contains((media.Path, target))) continue;
            Observe(FetchRewindFramesAsync(media, target, direction, step, fps, first, last));
            return;
        }
    }

    private async Task FetchRewindFramesAsync(MediaInfo media, long target, int direction, int step, double fps, long first, long last)
    {
        // FFmpeg 只能往後解，所以往回走時從前面開始解，一路解到 target。
        long start;
        int count;
        if (direction > 0)
        {
            start = target;
            count = (int)Math.Min(RewindBatchFrames, (last - target) / step + 1);
        }
        else
        {
            var back = Math.Min(RewindBatchFrames - 1, (target - first) / step);
            start = target - back * step;
            count = (int)back + 1;
        }

        _rewindFetching = true;
        try
        {
            var size = FrameExtractor.PreviewSize(media.DisplayWidth, media.DisplayHeight, MaxPreviewLongSide);
            var frames = await _extractor.ExtractRangeAsync(media.Path, start, count, step, fps, size);
            for (var i = 0; i < frames.Count; i++) _frames.Add((media.Path, start + i * step), frames[i]);
            // 來源結尾附近可能解不出影格，記下來，之後不再重試。
            if (frames.Count == 0) _missingFrames.Add((media.Path, target));
        }
        catch (Exception e) when (e is FfmpegException or System.ComponentModel.Win32Exception)
        {
            StopRewind();
            Error?.Invoke($"無法解出倒帶影格：{e.Message}");
        }
        finally
        {
            _rewindFetching = false;
        }
        if (IsRewinding) ShowRewindFrame();
    }

    private void PresentVlcFrame()
    {
        // 不顯示時也一定要取走影格：sink 在影格被取走前不會再通知，
        // 若在暫停、切換的瞬間直接 return，之後播放時畫面就永遠停住。
        var bitmap = _acceptVlcFrames ? _bitmap : null;
        _sink.CopyLatest((pixels, stride) =>
        {
            if (bitmap == null) return;
            // 切片段時可能還有舊尺寸的影格在路上，尺寸不符就跳過。
            if (pixels.Length != bitmap.PixelHeight * stride || stride != bitmap.PixelWidth * 4) return;
            bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, stride, 0);
        });
    }

    /// <summary>片段在來源中的第一格與最後一格編號。</summary>
    private static (long First, long Last) FrameRange(TimelineClip clip, double fps)
    {
        var first = ReverseGraph.FirstFrameAtOrAfter(clip.SourceIn, fps);
        return (first, Math.Max(first, ReverseGraph.FirstFrameAtOrAfter(clip.SourceOut, fps) - 1));
    }

    private bool HasFrame(string path, long frame) => _frames.TryGet((path, frame), out _) || _missingFrames.Contains((path, frame));

    /// <param name="sourceStep">逐格移動時為來源影格的移動方向（+1／-1），否則為 0。</param>
    private void ShowFrameAtPosition(int sourceStep = 0)
    {
        var request = ++_latestRequest;
        var clip = _timeline.VideoClipAt(Position);
        if (clip == null)
        {
            _pendingFrame = null;
            _shownRequest = request;
            ShowBlack();
            return;
        }

        var media = clip.Media;
        var fps = media.FrameRate.Value > 0 ? media.FrameRate.Value : DefaultFrameRate;
        var frame = FrameExtractor.FrameIndex(clip.ToSourceTime(Position), fps);
        var size = FrameExtractor.PreviewSize(media.DisplayWidth, media.DisplayHeight, MaxPreviewLongSide);
        var (first, last) = FrameRange(clip, fps);
        if (_frames.TryGet((media.Path, frame), out var cached))
        {
            _pendingFrame = null;
            _shownRequest = request;
            ShowFrame(cached, media, clip.Transform);
            if (sourceStep != 0) PrefetchStepFrames(media, frame, sourceStep, fps, size, first, last);
            return;
        }

        // 逐格移動時，連同同方向接下來的幾格一起解，下一步就直接從快取顯示。
        var batch = sourceStep == 0 ? null : FrameBatch.Toward(frame, sourceStep, first, last, StepBatchFrames);
        _pendingFrame = new FrameRequest(media, clip.Transform, size, fps, frame, request, batch);
        // 背景正在預先解下一批時中止它：一次只跑一個 FFmpeg，不中止的話要等那一批解完才輪到這一格。
        _prefetchCancel?.Cancel();
        if (!_extracting) Observe(ExtractPendingFramesAsync());
    }

    /// <summary>逐格移動中，快取快用完時先在背景解下一批；正在解別的影格時不插隊。</summary>
    private void PrefetchStepFrames(MediaInfo media, long frame, int sourceStep, double fps, Size2 size, long first, long last)
    {
        if (_extracting || _pendingFrame != null) return;
        if (FrameBatch.FirstMissing(frame, sourceStep, first, last, StepPrefetchLead, f => HasFrame(media.Path, f)) is not { } missing) return;
        _pendingFrame = new FrameRequest(media, FrameTransform.Identity, size, fps, missing, 0,
            FrameBatch.Toward(missing, sourceStep, first, last, StepBatchFrames), Show: false);
        Observe(ExtractPendingFramesAsync());
    }

    /// <summary>
    /// 背景工作不等結果。預料之外的錯誤若不處理就會被默默吞掉，使用者只看到畫面不更新；
    /// 在這裡轉成錯誤訊息。已知的錯誤（FFmpeg 失敗等）各工作自己處理，不會走到這裡。
    /// </summary>
    private void Observe(Task task) =>
        task.ContinueWith(t => _dispatcher.BeginInvoke(() => Error?.Invoke($"預覽發生錯誤：{t.Exception!.GetBaseException().Message}")),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>一次只跑一個 FFmpeg，並只保留最新的請求，拖曳時才不會排隊落後。</summary>
    private async Task ExtractPendingFramesAsync()
    {
        _extracting = true;
        try
        {
            while (_pendingFrame is { } request)
            {
                _pendingFrame = null;
                var key = (request.Media.Path, request.Frame);
                // 前一批（例如逐格預先解碼）可能已經解出這一格。
                RawFrame? frame;
                if (_frames.TryGet(key, out var cached)) frame = cached;
                else
                {
                    // 只有背景預先解碼可以被中止；要顯示的畫面一定解完。
                    using var cancel = request.Show ? null : new CancellationTokenSource();
                    _prefetchCancel = cancel;
                    try
                    {
                        frame = await ExtractRequestAsync(request, cancel?.Token ?? CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        continue; // 被要顯示的畫面插隊，這一批之後需要時再解
                    }
                    finally
                    {
                        _prefetchCancel = null;
                    }
                }
                if (frame == null)
                {
                    if (!request.Show) _missingFrames.Add(key);
                    continue;
                }
                // 拖曳中也顯示解好的影格，畫面才會跟著動；但不可蓋掉較新的畫面（如空白處的黑畫面）。
                if (!request.Show || IsPlaying || IsRewinding || request.Request < _shownRequest) continue;
                _shownRequest = request.Request;
                ShowFrame(frame, request.Media, request.Transform);
            }
        }
        catch (Exception e) when (e is FfmpegException or System.ComponentModel.Win32Exception)
        {
            Error?.Invoke($"無法解出預覽影格：{e.Message}");
        }
        finally
        {
            _extracting = false;
        }
    }

    /// <summary>解出請求的影格並放進快取；有 Batch 時一次解整批。</summary>
    private async Task<RawFrame?> ExtractRequestAsync(FrameRequest request, CancellationToken token)
    {
        var path = request.Media.Path;
        if (request.Batch is { } batch)
        {
            var frames = await _extractor.ExtractRangeAsync(path, batch.Start, batch.Count, 1, request.Fps, request.Size, token);
            for (var i = 0; i < frames.Count; i++) _frames.Add((path, batch.Start + i), frames[i]);
            if (_frames.TryGet((path, request.Frame), out var fromBatch)) return fromBatch;
        }

        var seek = FrameExtractor.FrameSeekTime(request.Frame, request.Fps);
        var frame = await _extractor.ExtractAsync(path, seek, request.Size, token)
            // 來源結尾附近可能定位不到影格，退回前一小段。
            ?? await _extractor.ExtractAsync(path, Math.Max(0, seek - 0.25), request.Size, token);
        if (frame != null) _frames.Add((path, request.Frame), frame);
        return frame;
    }

    private void ShowFrame(RawFrame frame, MediaInfo media, FrameTransform transform)
    {
        EnsureSurface(new Size2(frame.Width, frame.Height), media, transform);
        _bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
    }

    private void EnsureSurface(Size2 size, MediaInfo media, FrameTransform transform)
    {
        var changed = _surfaceMedia != media || _surfaceTransform != transform;
        if (_bitmap == null || _bitmap.PixelWidth != size.Width || _bitmap.PixelHeight != size.Height)
        {
            _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgr32, null);
            changed = true;
        }
        _surfaceMedia = media;
        _surfaceTransform = transform;
        if (changed) SurfaceChanged?.Invoke(_bitmap, media, transform);
    }

    private void ShowBlack()
    {
        if (_bitmap == null && _surfaceMedia == null) return;
        _bitmap = null;
        _surfaceMedia = null;
        SurfaceChanged?.Invoke(null, null, FrameTransform.Identity);
    }

    public void Dispose()
    {
        Stop();
        _timer.Stop();
        _rewindTimer.Stop();
        _commands.Wait(TimeSpan.FromSeconds(2));
        _videoPlayer.Dispose();
        _musicPlayer.Dispose();
        _vlc.Dispose();
        _sink.Dispose();
        _proxies.Dispose();
    }
}
