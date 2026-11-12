using System.Globalization;
using VideoEditor.Core.Models;

namespace VideoEditor.Core.Ffmpeg;

/// <summary>用 FFmpeg 解出素材的聲音，邊讀邊算成 <see cref="WaveformData"/>。</summary>
public sealed class WaveformExtractor(FfmpegTools tools)
{
    private const int DefaultSampleRate = 48000;

    /// <summary>
    /// 第一條音軌的波形。保留原本的取樣率（降取樣會先低通濾波，鼓、鈸的尖峰會被削掉）；
    /// 不混成單聲道（左右反相時會互相抵消），超過兩聲道才先混成立體聲。
    /// aresample 的 async 讓開頭晚開始的聲音與中途的空缺補成靜音，波形的時間才與素材時間一致。
    /// </summary>
    public async Task<WaveformData> ExtractAsync(MediaInfo media, CancellationToken token = default)
    {
        if (!media.HasAudio) throw new InvalidOperationException("素材沒有音訊。");
        var rate = media.AudioSampleRate > 0 ? media.AudioSampleRate : DefaultSampleRate;
        var channels = media.AudioChannels is 1 ? 1 : 2;
        string[] args =
        [
            "-v", "error", "-i", media.Path, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", "aresample=async=1:first_pts=0", "-ac", Int(channels), "-ar", Int(rate),
            "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1",
        ];
        var builder = new WaveformBuilder(rate, channels);
        await ProcessRunner.RunWithStreamAsync(tools.Ffmpeg, args, async output =>
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await output.ReadAsync(buffer, CancellationToken.None)) > 0)
                builder.Add(buffer.AsSpan(0, read));
        }, token);
        return builder.Build();
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
