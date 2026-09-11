using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO.MemoryMappedFiles;
using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using AudioPlayer.Interop;
using System.Runtime.InteropServices;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

FFmpeg.AutoGen.ffmpeg.RootPath = AppContext.BaseDirectory;
string path = Path.GetFullPath("_tools/test_tone.wav");
var engine = (PlaybackEngine)RuntimeHelpers.GetUninitializedObject(typeof(PlaybackEngine));
using (var session = Session.Open(engine, path, RenderKind.Pcm, 88200, 6, 300)!)
{
    var scratch = new double[4096 * session.Channels];
    for (int i = 0; i < 5000 && !session.IsDrained; i++)
    {
        session.FillPcm(scratch, 4096);
        Thread.Sleep(1);
    }
    var thread = (Thread)typeof(Session).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    thread.Join(1000);
    Console.WriteLine($"PCM EOF: drained={session.IsDrained}, decoderAlive={thread.IsAlive}");
    session.RequestSeek(0);
    Thread.Sleep(100);
    Console.WriteLine($"PCM seek after EOF: ready={session.ReadyFrames}, decoderAlive={thread.IsAlive}, drained={session.IsDrained}");
}
TimeoutProbe.Run();

foreach (int rate in new[] { 44100, 48000, 96000 })
{
    using var decoder = new PcmDecoder();
    if (!decoder.Open(path, 88200, 0, rate, 2)) throw new Exception("open failed");
    var samples = new double[32768];
    long count = 0;
    int n;
    while ((n = decoder.Read(samples)) > 0) count += n;
    Console.WriteLine($"Resample {rate}: durationMs={decoder.TotalMs}, frames={count}, expectedFromDuration={decoder.TotalMs * rate / 1000}");
}
using (var mmf = MemoryMappedFile.CreateNew(null, IpcConstants.MmfSize))
using (var view = mmf.CreateViewAccessor())
{
    var commands = new[] { CommandId.UpdateEq, CommandId.UpdateSettings, CommandId.UpdateDsp };
    for (int i = 0; i < commands.Length; i++)
    {
        IpcEnvelope.WriteCommand(view, IpcConstants.RequestBufferOffset, commands[i], (byte)(i + 1), []);
        IpcEnvelope.PublishVersion(view, IpcConstants.RequestVersionOffset, i + 1);
    }
    Console.WriteLine($"Mailbox after three publishes before reader scheduled: version={IpcEnvelope.ReadVersion(view, IpcConstants.RequestVersionOffset)}, command={IpcEnvelope.ReadCommandId(view, IpcConstants.RequestBufferOffset)}");
}

internal static unsafe class TimeoutProbe
{
    private static readonly ManualResetEventSlim Continue = new(false);
    private static readonly ManualResetEventSlim Done = new(false);
    private static int callsWhileInitializing;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Initialize(IntPtr self, int mode, int flags, long duration, long periodicity, WAVEFORMATEX* format, void* guid)
    {
        Continue.Wait();
        Done.Set();
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StopOrReset(IntPtr self)
    {
        if (!Done.IsSet) Interlocked.Increment(ref callsWhileInitializing);
        return 0;
    }
    internal static void Run()
    {
        void** table = (void**)NativeMemory.AllocZeroed(15 * (nuint)IntPtr.Size);
        void** native = (void**)NativeMemory.Alloc((nuint)IntPtr.Size);
        *native = table;
        table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)&Initialize;
        table[11] = table[12] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&StopOrReset;
        var output = new WasapiOutput(false, false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WasapiOutput).GetField("_client", flags)!.SetValue(output, new RawAudioClient((IntPtr)native));
        var format = WAVEFORMATEXTENSIBLE.Create(44100, 2, 32, 32, SubFormats.IeeeFloat);
        object? result = typeof(WasapiOutput).GetMethod("InitializeWithTimeout", flags)!.Invoke(output,
            [0, 0, 1000000L, 0L, Pointer.Box(&format, typeof(WAVEFORMATEXTENSIBLE*))]);
        output.Dispose();
        Console.WriteLine($"WASAPI blocked Initialize: result=0x{(int)result!:X8}, initTimedOut={typeof(WasapiOutput).GetField("_initTimedOut", flags)!.GetValue(output)}, Stop/Reset calls while worker active={callsWhileInitializing}");
        Continue.Set();
        Done.Wait();
        // Fake vtable intentionally retained until process exit, avoiding a test cleanup race.
    }
}

