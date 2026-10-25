using System.Runtime.InteropServices;

namespace AudioPlayer.Interop;

/// <summary>WavPack 5 的 C ABI；原生 DSD 样本位于 int32 的低八位，字节内最高有效位优先。</summary>
internal static unsafe class WavPackNative
{
    private const string Library = "wavpackdll.dll";
    internal const int OpenFileUtf8 = 0x80;
    internal const int OpenDsdNative = 0x100;
    internal const int QModeDsdAudio = 0x30;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr WavpackOpenFileInput([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte* error, int flags, int normOffset);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr WavpackCloseFile(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WavpackGetQualifyMode(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WavpackGetNumChannels(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint WavpackGetSampleRate(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long WavpackGetNumSamples64(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long WavpackGetSampleIndex64(IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint WavpackUnpackSamples(IntPtr context, int* samples, uint frames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WavpackSeekSample64(IntPtr context, long sample);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WavpackGetNumErrors(IntPtr context);
}
