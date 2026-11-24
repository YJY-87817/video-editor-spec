namespace VideoEditor.Core.Ffmpeg;

/// <summary>交給 FFmpeg／ffprobe 的輸入選項。</summary>
public static class FfmpegInput
{
    /// <summary>
    /// 只允許讀本機檔案，放在每個 <c>-i</c> 之前。FFmpeg 依檔案內容判斷格式，
    /// 精心製作的播放清單類檔案（可偽裝成 .mp4）可能讓它對外發出網路請求；程式只處理本機檔案，不需要其他協定。
    /// 這只擋網路協定，不能防止被導向讀取其他本機檔案。
    /// </summary>
    public static readonly string[] LocalFilesOnly = ["-protocol_whitelist", "file"];

    /// <summary>
    /// 圖片的透明部分鋪黑：預乘透明度後，完全透明的像素就是黑色，半透明的變暗，再丟掉透明度。
    /// 匯出、預覽與縮圖都用這一個，畫面才會一致；少了它，透明處原本藏著的顏色會露出來（透明度被忽略或 JPEG 縮圖丟掉透明度時）。
    /// </summary>
    public const string StillFlatten = "format=rgba,premultiply=inplace=1,format=rgb24";
}
