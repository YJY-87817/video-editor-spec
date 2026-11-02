using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;
using VideoEditor.Core.Timeline;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using TimelineModel = VideoEditor.Core.Timeline.Timeline;

namespace VideoEditor.App.Preview;

/// <summary>
/// 預覽引擎。播放時由 LibVLC 解碼（影片與配樂各一個播放器），
/// 暫停與拖曳時由 FFmpeg 解出精確影格，因為 LibVLC 無法倒退逐格。
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

    private readonly FrameExtractor _extractor;
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
    private double _clockBase;
    private TimelineClip? _videoClip;
    private TimelineClip? _musicClip;
    private bool _waitingForFirstFrame;
    private long _framesAtSwitch;
    private bool _acceptVlcFrames;
    private (MediaInfo Media, double SourceTime, Size2 Size, long Frame, long Request)? _pendingFrame;
    private long _latestRequest;
    private long _shownRequest;
    private bool _extracting;

    public PreviewEngine(FrameExtractor extractor, Dispatcher dispatcher)
    {
        _extractor = extractor;
        _dispatcher = dispatcher;
        _vlc = new LibVLC("--no-video-title-show", "--quiet");
        _videoPlayer = new MediaPlayer(_vlc);
        _musicPlayer = new MediaPlayer(_vlc);
        _sink.Attach(_videoPlayer);
        _sink.FrameAvailable = () => _dispatcher.BeginInvoke(PresentVlcFrame, DispatcherPriority.Render);
        _timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public double Position { get; private set; }
    public bool IsPlaying { get; private set; }
    public double Duration => _timeline.Duration;

    public event Action? PositionChanged;
    public event Action? PlayingChanged;
    /// <summary>顯示的素材或畫面尺寸改變。bitmap 為 null 表示時間軸空白處，顯示黑畫面。</summary>
    public event Action<WriteableBitmap?, MediaInfo?>? SurfaceChanged;
    public event Action<string>? Error;

    public void Load(TimelineModel timeline, double position = 0)
    {
        Stop();
        _timeline = timeline;
        Seek(position);
    }

    public void Play()
    {
        if (IsPlaying || Duration <= 0) return;
        if (Position >= Duration - 0.01) Position = 0;
        IsPlaying = true;
        _acceptVlcFrames = true;
        _clockBase = Position;
        _videoClip = null;
        _musicClip = null;
        _waitingForFirstFrame = false;
        _clock.Restart();
        _timer.Start();
        PlayingChanged?.Invoke();
        Tick();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        Stop();
        ShowFrameAtPosition();
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>移動播放頭。播放中拖曳會先暫停。</summary>
    public void Seek(double time)
    {
        Stop();
        Position = Math.Clamp(time, 0, Duration);
        PositionChanged?.Invoke();
        ShowFrameAtPosition();
    }

    public void StepFrame(int direction)
    {
        Stop();
        var clip = _timeline.VideoClipAt(Position);
        var fps = clip != null && clip.Media.FrameRate.Value > 0 ? clip.Media.FrameRate.Value : DefaultFrameRate;
        var next = clip == null
            ? Position + direction / fps
            : clip.Start + (FrameExtractor.FrameIndex(clip.ToSourceTime(Position), fps) + direction) / fps - clip.SourceIn;
        Seek(next);
    }

    /// <summary>時間軸內容改變（拖曳、修剪、刪除）後，重畫目前位置的影格。</summary>
    public void Refresh() => Seek(Math.Min(Position, Duration));

    private void Stop()
    {
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
        PlayingChanged?.Invoke();
    }

    private void Tick()
    {
        if (!IsPlaying) return;

        if (_waitingForFirstFrame
            && (_sink.FramesDisplayed > _framesAtSwitch || _firstFrameWait.ElapsedMilliseconds > FirstFrameTimeoutMs))
        {
            // 新片段的第一格出現後，時鐘才繼續走，畫面和位置才不會錯開。
            _waitingForFirstFrame = false;
            _clock.Restart();
        }

        var position = _waitingForFirstFrame ? _clockBase : _clockBase + _clock.Elapsed.TotalSeconds;
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

        ApplyVolume(_videoPlayer, _videoClip);
        ApplyVolume(_musicPlayer, _musicClip);
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
        EnsureSurface(size, clip.Media);
        var sourceTime = clip.ToSourceTime(position);
        var sourceOut = clip.SourceOut;
        var path = clip.Media.Path;
        _clockBase = position;
        _framesAtSwitch = _sink.FramesDisplayed;
        _waitingForFirstFrame = true;
        _firstFrameWait.Restart();
        Enqueue(() =>
        {
            _sink.SetTarget(size);
            PlayMedia(_videoPlayer, path, sourceTime, sourceOut);
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
            if (actual > 0 && Math.Abs(actual - expected) > MusicDriftLimitMs)
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
        var expected = _videoClip.ToSourceTime(position) * 1000;
        if (Math.Abs(actual - expected) <= VideoDriftLimitMs) return position;

        var corrected = _videoClip.Start + actual / 1000.0 - _videoClip.SourceIn;
        _clockBase = corrected;
        _clock.Restart();
        return corrected;
    }

    private static void ApplyVolume(MediaPlayer player, TimelineClip? clip)
    {
        if (clip == null) return;
        var volume = VolumeCurve.ToVlcVolume(clip.Volume);
        if (player.Volume != volume) player.Volume = volume;
    }

    private void PlayMedia(MediaPlayer player, string path, double start, double stop, params string[] options)
    {
        using var media = new Media(_vlc, path);
        media.AddOption(":start-time=" + start.ToString(CultureInfo.InvariantCulture));
        media.AddOption(":stop-time=" + stop.ToString(CultureInfo.InvariantCulture));
        foreach (var option in options) media.AddOption(option);
        player.Play(media);
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

    private void PresentVlcFrame()
    {
        if (!_acceptVlcFrames || _bitmap == null) return;
        var bitmap = _bitmap;
        _sink.CopyLatest((pixels, stride) =>
        {
            // 切片段時可能還有舊尺寸的影格在路上，尺寸不符就跳過。
            if (pixels.Length != bitmap.PixelHeight * stride || stride != bitmap.PixelWidth * 4) return;
            bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, stride, 0);
        });
    }

    private void ShowFrameAtPosition()
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
        if (_frames.TryGet((media.Path, frame), out var cached))
        {
            _pendingFrame = null;
            _shownRequest = request;
            ShowFrame(cached, media);
            return;
        }

        _pendingFrame = (media, FrameExtractor.FrameSeekTime(frame, fps), size, frame, request);
        if (!_extracting) _ = ExtractPendingFramesAsync();
    }

    /// <summary>一次只解一格，並只保留最新的請求，拖曳時才不會排隊落後。</summary>
    private async Task ExtractPendingFramesAsync()
    {
        _extracting = true;
        try
        {
            while (_pendingFrame is { } request)
            {
                _pendingFrame = null;
                var frame = await _extractor.ExtractAsync(request.Media.Path, request.SourceTime, request.Size)
                    // 來源結尾附近可能定位不到影格，退回前一小段。
                    ?? await _extractor.ExtractAsync(request.Media.Path, Math.Max(0, request.SourceTime - 0.25), request.Size);
                if (frame == null) continue;
                _frames.Add((request.Media.Path, request.Frame), frame);
                // 拖曳中也顯示解好的影格，畫面才會跟著動；但不可蓋掉較新的畫面（如空白處的黑畫面）。
                if (IsPlaying || request.Request < _shownRequest) continue;
                _shownRequest = request.Request;
                ShowFrame(frame, request.Media);
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

    private void ShowFrame(RawFrame frame, MediaInfo media)
    {
        EnsureSurface(new Size2(frame.Width, frame.Height), media);
        _bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
    }

    private void EnsureSurface(Size2 size, MediaInfo media)
    {
        var changed = _surfaceMedia != media;
        if (_bitmap == null || _bitmap.PixelWidth != size.Width || _bitmap.PixelHeight != size.Height)
        {
            _bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgr32, null);
            changed = true;
        }
        _surfaceMedia = media;
        if (changed) SurfaceChanged?.Invoke(_bitmap, media);
    }

    private void ShowBlack()
    {
        if (_bitmap == null && _surfaceMedia == null) return;
        _bitmap = null;
        _surfaceMedia = null;
        SurfaceChanged?.Invoke(null, null);
    }

    public void Dispose()
    {
        Stop();
        _timer.Stop();
        _commands.Wait(TimeSpan.FromSeconds(2));
        _videoPlayer.Dispose();
        _musicPlayer.Dispose();
        _vlc.Dispose();
        _sink.Dispose();
    }
}
