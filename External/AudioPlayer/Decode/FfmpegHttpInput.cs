using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

/// <summary>HTTP options and interrupt lifetime shared by PCM and raw DSD readers.
/// The decoder owns AVFormatContext and must close it before releasing this callback.</summary>
internal sealed unsafe class FfmpegHttpInput(PlaybackSource source, CancellationToken token) : IDisposable
{
    private readonly CancellationToken _token = token;
    private GCHandle _handle;
    private int _cancelled;
    private long _deadline;

    public AVFormatContext* AllocateContext(AVDictionary** options)
    {
        source.Validate();
        _deadline = Environment.TickCount64 + source.Buffer.OpenTimeoutMs;
        _handle = GCHandle.Alloc(this);
        ffmpeg.av_dict_set(options, "protocol_whitelist", "http,https,tcp,tls,httpproxy", 0);
        ffmpeg.av_dict_set(options, "tls_verify", "1", 0);
        ffmpeg.av_dict_set(options, "rw_timeout", ((long)source.Buffer.ReadTimeoutMs * 1000).ToString(CultureInfo.InvariantCulture), 0);
        ffmpeg.av_dict_set(options, "reconnect", source.Buffer.RetryCount > 0 ? "1" : "0", 0);
        ffmpeg.av_dict_set(options, "reconnect_on_network_error", source.Buffer.RetryCount > 0 ? "1" : "0", 0);
        ffmpeg.av_dict_set(options, "reconnect_max_retries", source.Buffer.RetryCount.ToString(CultureInfo.InvariantCulture), 0);
        ffmpeg.av_dict_set(options, "reconnect_delay_max", "2", 0);
        if (source.Headers.Count > 0)
            ffmpeg.av_dict_set(options, "headers", string.Concat(source.Headers.Select(x => x.Key + ": " + x.Value + "\r\n")), 0);
        var fmt = ffmpeg.avformat_alloc_context();
        if (fmt == null) throw new OutOfMemoryException();
        fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_handle);
        fmt->interrupt_callback.callback = new AVIOInterruptCB_callback_func
        { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&Interrupt };
        return fmt;
    }

    public void BeginRead() => Volatile.Write(ref _deadline, Environment.TickCount64 + source.Buffer.ReadTimeoutMs);
    public void Cancel() => Volatile.Write(ref _cancelled, 1);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Interrupt(void* opaque)
    {
        var input = (FfmpegHttpInput)GCHandle.FromIntPtr((nint)opaque).Target!;
        return Volatile.Read(ref input._cancelled) != 0 || input._token.IsCancellationRequested ||
            Environment.TickCount64 >= Volatile.Read(ref input._deadline) ? 1 : 0;
    }

    public void Dispose()
    {
        if (_handle.IsAllocated) _handle.Free();
    }
}
