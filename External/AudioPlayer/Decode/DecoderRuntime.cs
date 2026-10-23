using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

internal static class DecoderRuntime
{
    internal static void Initialize()
    {
        // 触发 AutoGen 绑定和四个原生库加载，不打开曲目或占用输出设备。
        // 同时验证依赖可用，避免 IPC 已就绪却要等首次 Play 才发现缺失的 DLL。
        _ = ffmpeg.avformat_version();
        _ = ffmpeg.avcodec_version();
        _ = ffmpeg.avutil_version();
        _ = ffmpeg.swresample_version();
    }
}
