using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Decode;
using AudioPlayer.Playback;

internal static unsafe partial class Program
{
    private static void RunWavPackTests()
    {
        foreach (var (channels, rate) in new[] { (1, 352800), (2, 352800), (2, 705600) })
        {
            int frames = rate * 2 + 3; // odd tail tests final DoP padding
            byte[] expected = new byte[frames * channels];
            new Random(42 + channels + rate).NextBytes(expected);
            string path = Path.Combine(AppContext.BaseDirectory, $"原始位流 {channels}ch {rate}.wv");
            WriteWavPack(path, expected, channels, rate, dsd: true);
            Run($"WV {channels}ch/{rate}: raw bytes, metadata and bounded reads", () =>
            {
                using var reader = new DsdRawReader();
                Require(reader.Open(path), "WV DSD reader did not open");
                Require(reader.Channels == channels && reader.ByteRatePerChannel == rate
                    && reader.DsdBitRate == rate * 8 && reader.DopSampleRate == rate / 2, "wrong DSD rate domain");
                Require(reader.TotalMs == 2000, "wrong duration");
                byte[] block = new byte[1003 * channels + channels - 1];
                int offset = 0;
                while (true)
                {
                    int count = reader.ReadInterleaved(block);
                    if (count == 0) break;
                    Require(count % channels == 0 && count <= block.Length, "partial/oversized frame");
                    Require(offset + count <= expected.Length && block.AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count)),
                        $"DSD bytes differ at {offset}");
                    offset += count;
                }
                Require(offset == expected.Length, $"truncated stream: {offset}/{expected.Length}");
                Require(reader.SeekToMs(517), "seek failed");
                int seekCount = reader.ReadInterleaved(block);
                int seekOffset = (int)Math.Round(517 * rate / 1000.0) * channels;
                Require(seekCount > 0 && block.AsSpan(0, seekCount).SequenceEqual(expected.AsSpan(seekOffset, seekCount)), "seek bytes differ");
                Require(reader.SeekToMs(0), "rewind failed");
                int rewindCount = reader.ReadInterleaved(block);
                Require(block.AsSpan(0, rewindCount).SequenceEqual(expected.AsSpan(0, rewindCount)), "rewind bytes differ");
                Require(reader.SeekToMs(99999) && reader.ReadInterleaved(block) == 0, "seek beyond EOF must stop");
                Require(reader.SeekToMs(-1) && reader.ReadInterleaved(block) > 0, "rewind after EOF failed");
            });
            foreach (var kind in new[] { RenderKind.NativeDsd, RenderKind.Dop })
                Run($"WV {channels}ch/{rate}: {kind} session payload, EOF and progress", () => CheckWvSession(path, expected, channels, rate, kind));
            foreach (string mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent", "WasapiShared" })
                Run($"WV {channels}ch/{rate}: {mode} selection", () => CheckWvSelection(path, mode, mode == "ASIO"
                    ? RenderKind.NativeDsd : mode == "WasapiShared" ? RenderKind.Pcm : RenderKind.Dop));
            Run($"WV {channels}ch/{rate}: bitstream disabled", () => CheckWvSelection(path, "ASIO", RenderKind.Pcm, false));
            Run($"WV {channels}ch/{rate}: ASIO DoP fallback", () => CheckWvSelection(path, "ASIO", RenderKind.Dop, kindOverride: RenderKind.Dop));
            Run($"WV {channels}ch/{rate}: bitstream EQ rejected", () =>
            {
                var engine = Engine("ASIO");
                engine.MusicUrl = path;
                // This is the predicate used by UpdateEq, before a session is opened as well.
                Require((bool)Invoke(engine, "IsBitstreamActive", path)!, "WV DSD did not activate bitstream EQ guard");
            });
        }
        string pcmPath = Path.Combine(AppContext.BaseDirectory, "普通 PCM.wv");
        WriteWavPack(pcmPath, new byte[44100 * 2], 2, 44100, dsd: false);
        foreach (string mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent" })
            Run($"PCM WV stays PCM in {mode}", () => CheckWvSelection(pcmPath, mode, RenderKind.Pcm));
        Run("PCM WV cannot be opened as raw DSD", () =>
        {
            using var reader = new DsdRawReader();
            Require(!reader.Open(pcmPath), "PCM accepted as raw DSD");
        });

        Run("LSBF-origin WV normalizes to MSB-first bytes", () =>
        {
            byte[] expected = new byte[44103 * 2];
            new Random(97).NextBytes(expected);
            string path = Path.Combine(AppContext.BaseDirectory, "LSBF 原始.WV");
            WriteWavPack(path, expected, 2, 352800, dsd: true, dsdQMode: 0x10);
            using var reader = new DsdRawReader();
            Require(reader.Open(path), "LSBF WV open failed");
            byte[] block = new byte[2002];
            int offset = 0, count;
            while ((count = reader.ReadInterleaved(block)) > 0)
            {
                Require(block.AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count)), "DSD bit order was reversed twice");
                offset += count;
            }
            Require(offset == expected.Length, "LSBF stream truncated");
            Require(!reader.Open(pcmPath), "reader reused PCM as DSD");
            Require(reader.Open(path), "reader failed to reopen after failed open");
        });
        Run("Invalid WV is rejected without leaking its file handle", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "invalid.wv");
            File.WriteAllText(path, "not a WavPack file");
            using var reader = new DsdRawReader();
            Require(!reader.Open(path), "invalid WV accepted");
            Require(!(bool)Invoke(Engine("ASIO"), "IsBitstreamActive", path)!, "invalid WV identified as DSD");
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
    }

    private static void CheckWvSelection(string path, string mode, RenderKind expected, bool dop = true, RenderKind? kindOverride = null)
    {
        var engine = Engine(mode);
        engine.IsDopEnabled = dop;
        using var old = Source(engine, RenderKind.Pcm);
        Set(engine, "_session", old);
        engine.MusicUrl = path;
        using var next = (Session?)Invoke(engine, "OpenSession", path, false, kindOverride);
        Require(next != null && next.Kind == expected, $"expected {expected}, got {next?.Kind.ToString() ?? "null"}");
        CheckProgress(next!);
    }

    private static void CheckWvSession(string path, byte[] expected, int channels, int rate, RenderKind kind)
    {
        var engine = Engine("ASIO");
        using var session = Session.Open(engine, path, kind, 88200, 6, 300);
        Require(session != null, "session did not open");
        const int renderFrames = 997; // odd block sizes stress DoP phase continuity
        byte[] raw = new byte[renderFrames * channels];
        uint[] dop = new uint[renderFrames * channels];
        int consumedBytes = 0;
        int phase = 0;
        long deadline = Environment.TickCount64 + 10000;
        while (!session!.IsDrained && Environment.TickCount64 < deadline)
        {
            long before = session.FramesPlayed;
            if (kind == RenderKind.NativeDsd) session.FillDsdBytes(raw, renderFrames);
            else session.FillDop(dop, renderFrames);
            int audibleFrames = (int)(session.FramesPlayed - before);
            for (int f = 0; f < (kind == RenderKind.Dop ? renderFrames : audibleFrames); f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    if (kind == RenderKind.NativeDsd)
                        Require(consumedBytes < expected.Length && raw[f * channels + c] == expected[consumedBytes++], "native payload differs");
                    else
                    {
                        uint sample = dop[f * channels + c];
                        Require(sample >> 16 == ((phase + f) % 2 == 0 ? 0x05u : 0xfau), "DoP marker phase differs");
                        if (f < audibleFrames)
                        {
                            int offset = consumedBytes + c;
                            byte first = expected[offset];
                            byte second = offset + channels < expected.Length ? expected[offset + channels] : (byte)0x69;
                            Require((sample & 0xffff) == ((uint)first << 8 | second), "DoP payload differs");
                        }
                    }
                }
                if (kind == RenderKind.Dop && f < audibleFrames) consumedBytes += channels * 2;
            }
            phase += renderFrames;
            if (audibleFrames == 0) Thread.Sleep(1);
        }
        Require(session.IsDrained, "session stalled before EOF");
        int padded = kind == RenderKind.Dop ? expected.Length + channels : expected.Length;
        Require(consumedBytes == padded, $"payload truncated: {consumedBytes}/{padded}");
        Require(Math.Abs(session.CurrentMs - 2000) <= 1, "wrong playback progress");

        // The decoder may reach EOF while its last buffered frames are still playing.
        // Seeking must revive production even after the source has been fully read.
        session.RequestSeek(517);
        deadline = Environment.TickCount64 + 2000;
        while (session.FramesPlayed == 0 && Environment.TickCount64 < deadline)
        {
            if (kind == RenderKind.NativeDsd) session.FillDsdBytes(raw, renderFrames);
            else session.FillDop(dop, renderFrames);
            if (session.FramesPlayed == 0) Thread.Sleep(1);
        }
        Require(session.FramesPlayed > 0, "seek after decode EOF stalled");
        int seekOffset = (int)Math.Round(517 * rate / 1000.0) * channels;
        if (kind == RenderKind.NativeDsd)
            Require(raw.AsSpan().SequenceEqual(expected.AsSpan(seekOffset, raw.Length)), "session seek payload differs");
        else
            for (int f = 0; f < renderFrames; f++)
                for (int c = 0; c < channels; c++)
                {
                    int offset = seekOffset + f * channels * 2 + c;
                    Require((dop[f * channels + c] & 0xffff) == ((uint)expected[offset] << 8 | expected[offset + channels]),
                        "DoP session seek payload differs");
                }
    }

    // Fixture encoding only. Production imports are decode-only.
    [StructLayout(LayoutKind.Sequential)]
    private struct WvConfig
    {
        public float Bitrate, ShapingWeight;
        public int BitsPerSample, BytesPerSample, QMode, Flags, XMode, Channels, FloatNormExp;
        public int BlockSamples, WorkerThreads, SampleRate, ChannelMask;
        public fixed byte Md5[16];
        public byte Md5Read;
        public int TagCount;
        public IntPtr Tags;
    }

    private static void WriteWavPack(string path, byte[] source, int channels, int rate, bool dsd, int dsdQMode = 0x20)
    {
        using var stream = File.Create(path);
        var handle = GCHandle.Alloc(stream);
        IntPtr context = IntPtr.Zero;
        try
        {
            context = WavpackOpenFileOutput(&WriteWvBlock, GCHandle.ToIntPtr(handle), IntPtr.Zero);
            Require(context != IntPtr.Zero, "fixture encoder open failed");
            var config = new WvConfig { BitsPerSample = 8, BytesPerSample = 1, QMode = dsd ? dsdQMode : 0,
                Channels = channels, SampleRate = rate, ChannelMask = channels == 1 ? 4 : 3, BlockSamples = 8192 };
            Require(WavpackSetConfiguration64(context, &config, source.Length / channels, null) != 0, "fixture configuration failed");
            Require(WavpackPackInit(context) != 0, "fixture encoder init failed");
            int[] samples = new int[8192 * channels];
            for (int offset = 0; offset < source.Length;)
            {
                int count = Math.Min(samples.Length, source.Length - offset);
                for (int i = 0; i < count; i++) samples[i] = source[offset + i];
                fixed (int* ptr = samples) Require(WavpackPackSamples(context, ptr, (uint)(count / channels)) != 0, "fixture encode failed");
                offset += count;
            }
            Require(WavpackFlushSamples(context) != 0, "fixture flush failed");
        }
        finally
        {
            if (context != IntPtr.Zero) WavpackCloseFile(context);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteWvBlock(IntPtr id, void* data, int count)
    {
        try { ((FileStream)GCHandle.FromIntPtr(id).Target!).Write(new ReadOnlySpan<byte>(data, count)); return 1; }
        catch { return 0; }
    }
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WavpackOpenFileOutput(delegate* unmanaged[Cdecl]<IntPtr, void*, int, int> output, IntPtr id, IntPtr correction);
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WavpackSetConfiguration64(IntPtr context, WvConfig* config, long samples, byte* channelIds);
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WavpackPackInit(IntPtr context);
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WavpackPackSamples(IntPtr context, int* samples, uint frames);
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WavpackFlushSamples(IntPtr context);
    [DllImport("wavpackdll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WavpackCloseFile(IntPtr context);
}
