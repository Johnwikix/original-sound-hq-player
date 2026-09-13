using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioPlayer.Decode;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private const string Eac3OracleHash = "5F20669A2704B430EBF0D4DE886E755019375142D1B1F5295E133C4E07E4FC7F";
    private static WAVEFORMATEXTENSIBLE_IEC61937 _capturedIecFormat;

    private static void CheckAtmosFile(string path, string expectedHash)
    {
        using var reader = new Eac3BitstreamReader();
        Require(reader.Open(path), "E-AC-3 bitstream reader did not open");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] burst = new byte[Eac3BitstreamReader.BurstBytes];
        byte[]? first = null;
        long count = 0;
        while (reader.ReadBurst(burst) != 0)
        {
            Require(BinaryPrimitives.ReadUInt16LittleEndian(burst) == 0xF872
                && BinaryPrimitives.ReadUInt16LittleEndian(burst.AsSpan(2)) == 0x4E1F
                && BinaryPrimitives.ReadUInt16LittleEndian(burst.AsSpan(4)) == 0x15, "bad IEC preamble/type");
            int payload = BinaryPrimitives.ReadUInt16LittleEndian(burst.AsSpan(6));
            Require(payload > 0 && payload <= burst.Length - 8 && (payload & 1) == 0, "invalid payload byte count");
            Require(burst.AsSpan(payload + 8).IndexOfAnyExcept((byte)0) < 0, "nonzero burst padding");
            first ??= burst.ToArray();
            hash.AppendData(burst);
            count++;
        }
        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        Require(actualHash == expectedHash, $"IEC stream differs from FFmpeg oracle: {actualHash}");
        Require(reader.ReadBurst(burst) == 0, "EOF not stable");
        Require(reader.SeekToMs(0) && reader.ReadBurst(burst) > 0 && burst.AsSpan().SequenceEqual(first), "rewind changed encoded payload");
        Require(reader.SeekToMs(reader.TotalMs / 2) && reader.ReadBurst(burst) > 0, "seek failed");
        Console.WriteLine($"IEC61937 exact match: {count} bursts, Atmos={reader.IsAtmos}, SHA256={actualHash}");
    }

    private static void RunAtmosTests(string root)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "eac3-5.1.m4a");
        Run("Atmos transport: E-AC-3 bursts equal independent FFmpeg reference", () => CheckAtmosFile(path, Eac3OracleHash));
        Run("Atmos transport: full ring never publishes a partial compressed burst", () =>
        {
            const int frames = Eac3BitstreamReader.BurstFrames;
            var ring = new Iec61937Ring(frames + 1, 0);
            byte[] burst = new byte[Eac3BitstreamReader.BurstBytes];
            Random.Shared.NextBytes(burst);
            Require(ring.Push(burst, frames, () => false), "initial write failed");
            using var entered = new ManualResetEventSlim();
            using var cancelled = new CancellationTokenSource();
            var producer = Task.Run(() => ring.Push(burst, frames, () =>
            {
                entered.Set();
                return cancelled.IsCancellationRequested;
            }));
            try
            {
                Require(entered.Wait(2000), "producer did not start");
                Require(ring.ReadyFrames == frames, "partial burst became visible");
                byte[] output = new byte[burst.Length];
                Require(ring.Render(output, frames) == frames && output.AsSpan().SequenceEqual(burst), "first burst altered");
                Require(producer.Wait(2000) && producer.Result, "producer did not resume");
                Require(ring.Render(output, frames) == frames && output.AsSpan().SequenceEqual(burst), "second burst altered");
            }
            finally
            {
                cancelled.Cancel();
                ring.WakeProducer();
                producer.Wait(2000);
            }
        });
        Run("Atmos transport: session has no DSP/gain and preserves every burst through the ring", () =>
        {
            using var session = Session.Open(Engine("WasapiExclusiveEvent"), path, RenderKind.Eac3, 88200, 0, 100)!;
            Require(session != null && session.Gain == null && session.Effects == null && session.SampleRate == 192000
                && session.Channels == 2, "incorrect carrier or PCM effects attached");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] burst = new byte[Eac3BitstreamReader.BurstBytes];
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!session!.IsDrained && timeout.ElapsedMilliseconds < 5000)
            {
                if (session.ReadyFrames < Eac3BitstreamReader.BurstFrames) { Thread.Sleep(1); continue; }
                long before = session.FramesPlayed;
                session.FillIec61937(burst, Eac3BitstreamReader.BurstFrames);
                // The render contract emits silence while prebuffering without consuming input.
                if (session.FramesPlayed == before) { Thread.Sleep(1); continue; }
                hash.AppendData(burst);
            }
            Require(session.IsDrained && Convert.ToHexString(hash.GetHashAndReset()) == Eac3OracleHash, "ring altered/lost encoded data");
            session.RequestSeek(0);
            Require(SpinWait.SpinUntil(() =>
            {
                if (session.ReadyFrames < Eac3BitstreamReader.BurstFrames) return false;
                session.FillIec61937(burst, Eac3BitstreamReader.BurstFrames);
                return session.FramesPlayed > 0;
            }, 2000), "session seek did not refill");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(burst) == 0xF872, "seek lost burst alignment");
        });
        foreach (string mode in new[] { "WasapiExclusiveEvent", "WasapiExclusivePush", "ASIO", "WasapiShared", "DirectSound" })
        foreach (bool enabled in new[] { false, true })
            Run($"Atmos transport: {mode} opt-in={enabled} scope", () =>
            {
                var engine = Engine(mode);
                engine.ExperimentalAtmosPassthrough = enabled;
                using var session = (Session?)Invoke(engine, "OpenSession", path, false, null);
                Require(session?.Kind == (enabled && mode.StartsWith("WasapiExclusive") ? RenderKind.Eac3 : RenderKind.Pcm), "experimental path escaped scope");
                using var ordinary = (Session?)Invoke(engine, "OpenSession", Path.Combine(root, "_tools", "test_tone.wav"), false, null);
                Require(ordinary?.Kind == RenderKind.Pcm && ordinary.Channels == 2, "ordinary PCM behavior changed");
            });
        Run("Atmos transport: compressed format has 52-byte extension, proper carrier and encoded layout", () =>
        {
            foreach (bool atmos in new[] { false, true })
            {
                var format = WAVEFORMATEXTENSIBLE_IEC61937.Eac3(0x60F, atmos);
                Require(sizeof(WAVEFORMATEXTENSIBLE_IEC61937) == 52 && format.FormatExt.Format.cbSize == 34
                    && format.FormatExt.Format.nSamplesPerSec == 192000 && format.FormatExt.Format.nChannels == 2
                    && format.FormatExt.Format.nBlockAlign == 4 && format.FormatExt.Format.nAvgBytesPerSec == 768000
                    && format.EncodedSamplesPerSec == 48000 && format.EncodedChannelCount == 6
                    && format.FormatExt.dwChannelMask == 0x60F, "invalid IEC format metadata");
                Require(format.FormatExt.SubFormat.ToString() == (atmos ? "0000010a-0cea-0010-8000-00aa00389b71" : "0000000a-0cea-0010-8000-00aa00389b71"), "invalid codec GUID");
            }
        });
        Run("Atmos transport: Initialize worker receives entire IEC extension", () =>
        {
            using var native = new NativeObject(16);
            native.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)&CaptureIecInitialize;
            using var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer));
            try
            {
                var format = WAVEFORMATEXTENSIBLE_IEC61937.Eac3(0x60F, true);
                int hr = (int)Invoke(output, "InitializeWithTimeout", 1, 0, 320000L, 320000L,
                    Pointer.Box(&format, typeof(WAVEFORMATEXTENSIBLE*)))!;
                Require(hr == 0 && _capturedIecFormat.EncodedSamplesPerSec == 48000
                    && _capturedIecFormat.EncodedChannelCount == 6, "format tail truncated by async initialization");
            }
            finally { Set(output, "_client", null); }
        });
        Run("Atmos transport: WASAPI output is byte-exact and shared/ASIO reject compressed source", () =>
        {
            var source = new EncodedTestSource();
            using var shared = new WasapiOutput(false, false);
            Require(!shared.Start(-1, 100, source), "compressed bytes accepted by shared PCM");
            Require(AsioOutput.SelectOutputChannelCount(source, 8) == 0, "ASIO accepted compressed bytes");
            using var output = new WasapiOutput(true, false);
            Set(output, "_source", source); Set(output, "_channels", 2u);
            byte* bytes = stackalloc byte[128];
            Invoke(output, "FillEndpoint", Pointer.Box(bytes, typeof(byte*)), 32u);
            for (int i = 0; i < 128; i++) Require(bytes[i] == (byte)i, "endpoint transformed bitstream");
        });
        Run("Atmos transport: optional setting defaults off for older clients", () =>
        {
            byte[] bytes = new byte[BinarySerializer.IpcSettingSize];
            int size = BinarySerializer.WriteIpcSetting(bytes, new() { ExperimentalAtmosPassthrough = true, ExperimentalSurround51 = true });
            Require(BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, size)).ExperimentalAtmosPassthrough, "flag lost");
            var legacy = BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, size - 1));
            Require(!legacy.ExperimentalAtmosPassthrough && legacy.ExperimentalSurround51, "old 5.1 setting changed");
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CaptureIecInitialize(IntPtr self, int mode, int flags, long duration, long periodicity, WAVEFORMATEX* format, void* guid)
    {
        _capturedIecFormat = *(WAVEFORMATEXTENSIBLE_IEC61937*)format;
        return 0;
    }

    private sealed class EncodedTestSource : IRenderSource
    {
        public RenderKind Kind => RenderKind.Eac3;
        public int SampleRate => 192000;
        public int Channels => 2;
        public void FillIec61937(Span<byte> bytes, int frames) { for (int i = 0; i < frames * 4; i++) bytes[i] = (byte)i; }
        public void FillPcm(Span<double> buffer, int frames) => throw new InvalidOperationException("Encoded data entered PCM path");
        public void FillDop(Span<uint> buffer, int frames) => throw new InvalidOperationException("Encoded data entered DoP path");
        public void FillDsdBytes(Span<byte> buffer, int frames) => throw new InvalidOperationException("Encoded data entered DSD path");
    }
}
