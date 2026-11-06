using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using VideoEditor.Core.Models;

namespace VideoEditor.App.Preview;

/// <summary>
/// 接收 LibVLC 解出的影格。用回呼而非原生視窗，畫面才能畫進 WPF 的 Image，
/// 預覽框線也才能直接疊在上面（WPF 無法疊在原生視窗之上）。
/// </summary>
internal sealed class VlcVideoSink : IDisposable
{
    private readonly object _gate = new();
    private Size2 _target = new(2, 2);
    private IntPtr _buffer;
    private byte[] _latest = [];
    private int _stride;
    private bool _renderPending;
    private long _framesDisplayed;

    /// <summary>有新影格可取用時觸發（在 LibVLC 執行緒上）；已有待處理的影格時不重複觸發。</summary>
    public Action? FrameAvailable { get; set; }

    public long FramesDisplayed => Interlocked.Read(ref _framesDisplayed);

    public void Attach(MediaPlayer player)
    {
        player.SetVideoFormatCallbacks(OnFormat, OnCleanup);
        player.SetVideoCallbacks(OnLock, OnUnlock, OnDisplay);
    }

    public void SetTarget(Size2 size)
    {
        lock (_gate) _target = size;
    }

    /// <summary>讓下一個影格一定會觸發 FrameAvailable。</summary>
    public void ResetPending()
    {
        lock (_gate) _renderPending = false;
    }

    /// <summary>
    /// 取出最新影格。每次收到 FrameAvailable 都必須呼叫（即使不顯示），否則不會再收到通知。
    /// 呼叫方須在下一次取用前用完緩衝區。
    /// </summary>
    public void CopyLatest(Action<byte[], int> consume)
    {
        lock (_gate)
        {
            _renderPending = false;
            if (_latest.Length > 0) consume(_latest, _stride);
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        lock (_gate)
        {
            // 不用 LibVLC 建議的尺寸：它含解碼對齊的補白（例如 1088 高），會讓畫面比例跑掉。
            width = (uint)_target.Width;
            height = (uint)_target.Height;
            pitches = width * 4;
            lines = height;
            _stride = (int)pitches;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            var size = (int)(pitches * lines);
            _buffer = Marshal.AllocHGlobal(size);
            _latest = new byte[size];
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        bool notify;
        lock (_gate)
        {
            Marshal.Copy(_buffer, _latest, 0, _latest.Length);
            notify = !_renderPending;
            _renderPending = true;
        }
        Interlocked.Increment(ref _framesDisplayed);
        if (notify) FrameAvailable?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
