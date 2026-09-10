using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;

internal static unsafe partial class Program
{
    private static readonly List<long> SharedBufferDurations = new();
    private static bool _rejectDirectFormat;

    private static void RunBufferPolicyTests()
    {
        foreach (int preferred in new[] { 256, 1024, 2048 })
            Run($"ASIO: driver preference {preferred} precedes calculated buffer", () =>
            {
                var method = typeof(AsioOutput).GetMethod("BuildBufferCandidates", BindingFlags.Static | BindingFlags.NonPublic)!;
                var candidates = (int[])method.Invoke(null, [64, 8192, preferred, -1])!;
                Require(candidates[0] == preferred, $"first={candidates[0]}, driver preferred={preferred}");
            });
        foreach (bool fallback in new[] { false, true })
            foreach (int latency in new[] { 25, 100, 700 })
                Run($"WASAPI shared: {latency}ms reaches native Initialize (fallback={fallback})", () =>
                    CheckSharedBufferDuration(latency, fallback));
    }

    private static void CheckSharedBufferDuration(int latencyMs, bool fallback)
    {
        using var native = new NativeObject(15);
        native.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, Guid*, int>)&InitializeSharedClient;
        native.Table[4] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&SharedBufferSize;
        native.Table[8] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEX**, int>)&SharedMixFormat;
        using var source = Source(Engine("WasapiShared"), RenderKind.Pcm, 48000);
        var output = new WasapiOutput(false, false);
        Set(output, "_source", source);
        Set(output, "_clientPtr", native.Pointer);
        Set(output, "_client", new RawAudioClient(native.Pointer));
        SharedBufferDurations.Clear();
        _rejectDirectFormat = fallback;
        Require((bool)Invoke(output, "InitializeShared", source, latencyMs)!, "shared Initialize failed");
        Require(SharedBufferDurations.Count == (fallback ? 2 : 1), "wrong initialization path");
        Require(SharedBufferDurations.All(duration => duration == latencyMs * 10000L),
            $"requested {latencyMs}ms, native durations={string.Join(',', SharedBufferDurations)} hns");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InitializeSharedClient(IntPtr self, int mode, int flags, long duration, long period, WAVEFORMATEX* format, Guid* session)
    {
        SharedBufferDurations.Add(duration);
        return _rejectDirectFormat && SharedBufferDurations.Count == 1
            ? WasapiTypes.AudclntEUnsupportedFormat : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SharedBufferSize(IntPtr self, uint* frames) { *frames = 4800; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SharedMixFormat(IntPtr self, WAVEFORMATEX** format)
    {
        var mix = (WAVEFORMATEXTENSIBLE*)Marshal.AllocCoTaskMem(sizeof(WAVEFORMATEXTENSIBLE));
        *mix = WAVEFORMATEXTENSIBLE.Create(48000, 2, 32, 32, SubFormats.IeeeFloat);
        *format = (WAVEFORMATEX*)mix;
        return 0;
    }
}
