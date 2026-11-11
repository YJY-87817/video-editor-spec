using System.Text.Json;
using VideoEditor.Core.Export;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Models;

namespace VideoEditor.Tests;

/// <summary>整合測試共用的短片，由 FFmpeg 當場產生。</summary>
public sealed class TestMedia : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "VideoEditorTests", Guid.NewGuid().ToString("N"));
    public FfmpegTools Tools { get; } = FfmpegLocator.Locate();

    public string Landscape { get; }
    public string Portrait { get; }
    public string Rotated { get; }
    public string NoAudio { get; }
    public string Hdr { get; }
    public string Music { get; }

    public TestMedia()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Landscape = Make("landscape.mp4", "testsrc2=s=1920x1080:r=30:d=4", "sine=f=440:d=4", "-pix_fmt", "yuv420p");
        Portrait = Make("portrait.mp4", "testsrc2=s=1080x1920:r=30:d=2", "sine=f=500:d=2", "-pix_fmt", "yuv420p");
        NoAudio = Make("noaudio.mp4", "testsrc2=s=1280x720:r=25:d=2", null, "-pix_fmt", "yuv420p");
        Hdr = Make("hdr.mov", "testsrc2=s=1280x720:r=30:d=1", "sine=f=300:d=1",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p10le",
            "-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc");
        Music = Path.Combine(Directory, "music.m4a");
        Ffmpeg("-f", "lavfi", "-i", "sine=f=220:d=3", "-c:a", "aac", Music);
        Rotated = Path.Combine(Directory, "rotated.mp4");
        Ffmpeg("-display_rotation", "90", "-i", Landscape, "-c", "copy", Rotated);
    }

    private string Make(string name, string video, string? audio, params string[] options)
    {
        var path = Path.Combine(Directory, name);
        var args = new List<string> { "-f", "lavfi", "-i", video };
        if (audio != null) args.AddRange(["-f", "lavfi", "-i", audio, "-c:a", "aac"]);
        if (!options.Contains("-c:v")) args.AddRange(["-c:v", "libx264", "-preset", "ultrafast"]);
        args.AddRange(options);
        args.Add(path);
        Ffmpeg(args.ToArray());
        return path;
    }

    private void Ffmpeg(params string[] args) =>
        ProcessRunner.RunForBytesAsync(Tools.Ffmpeg, new[] { "-hide_banner", "-y", "-v", "error" }.Concat(args)).GetAwaiter().GetResult();

    public Task<MediaInfo> Probe(string path) => new MediaProbe(Tools).ProbeAsync(path);

    public string Output(string name) => Path.Combine(Directory, name);

    public async Task<MediaInfo> Export(VideoEditor.Core.Timeline.Timeline timeline, OutputSettings settings, string name)
    {
        var output = Output(name);
        var plan = ExportPlanner.Build(timeline, settings, output);
        await new ExportService(Tools).ExportAsync(plan, output, null);
        return await Probe(output);
    }

    /// <summary>測量指定區段的音量（dB）。</summary>
    public async Task<double> MeanVolume(string path, double start, double length)
    {
        var args = new[] { "-hide_banner", "-ss", start.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", length.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-af", "volumedetect", "-vn", "-f", "null", "-" };
        var info = new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"mean_volume: (-?[\d.]+|-inf) dB");
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>產生測試專用的短片（lavfi 來源），參數同共用短片。</summary>
    public string MakeClip(string name, string video, string? audio, params string[] options) => Make(name, video, audio, options);

    /// <summary>
    /// 執行 action 期間，量測參數裡含有 marker 的 FFmpeg 程序用過的最大記憶體（位元組）。
    /// 以 marker 篩選，才不會算到同時在跑的其他測試。
    /// </summary>
    public static async Task<long> PeakMemory(string marker, Func<Task> action)
    {
        long peak = 0;
        var watchers = new System.Collections.Concurrent.ConcurrentBag<Task>();
        void OnStarted(System.Diagnostics.Process process)
        {
            if (!process.StartInfo.ArgumentList.Any(arg => arg.Contains(marker, StringComparison.Ordinal))) return;
            watchers.Add(Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        process.Refresh();
                        var now = Math.Max(process.PeakWorkingSet64, process.WorkingSet64);
                        long seen;
                        while (now > (seen = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                        await Task.Delay(5);
                    }
                }
                // 程序在讀取途中結束或已被釋放：量到這裡為止。
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }));
        }

        ProcessRunner.Started += OnStarted;
        try { await action(); }
        finally { ProcessRunner.Started -= OnStarted; }
        await Task.WhenAll(watchers);
        return Interlocked.Read(ref peak);
    }

    /// <summary>產生單色短片，pixelFormat 用 yuvj420p 即為全範圍（full range）來源。</summary>
    public string MakeSolid(string name, string color, string pixelFormat, double duration = 1) =>
        Make(name, $"color={color}:s=640x360:r=30:d={duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}", null,
            "-vf", $"format={pixelFormat}", "-pix_fmt", pixelFormat);

    /// <summary>指定時間那一格的 Y 最小與最大值，讀的是檔案裡的原始像素值，不做色彩範圍轉換。</summary>
    public async Task<(int Min, int Max)> LumaRange(string path, double time)
    {
        var args = new[] { "-hide_banner", "-ss", time.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vf", "signalstats,metadata=print", "-frames:v", "1", "-f", "null", "-" };
        var info = new System.Diagnostics.ProcessStartInfo(Tools.Ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        int Value(string key) => int.Parse(System.Text.RegularExpressions.Regex.Match(stderr, $@"lavfi\.signalstats\.{key}=(\d+)").Groups[1].Value);
        return (Value("YMIN"), Value("YMAX"));
    }

    /// <summary>計算輸出影片的影格數。</summary>
    public async Task<int> CountFrames(string path)
    {
        var json = await ProcessRunner.RunForBytesAsync(Tools.Ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        return int.Parse(document.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString()!);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, true); }
        catch (IOException) { }
    }
}

[CollectionDefinition("media")]
public sealed class MediaCollection : ICollectionFixture<TestMedia>;
