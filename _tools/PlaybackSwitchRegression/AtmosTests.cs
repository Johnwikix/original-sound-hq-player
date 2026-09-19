using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioPlayer.Decode;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

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
        var cache = new AtmosProbeCache();
        using (var initial = cache.TryOpen(path)) Require(initial != null, "cache warm-up failed");
        using var cached = cache.TryOpen(path);
        Require(cached != null && !cached.ProbeCompleted && cached.IsAtmos == reader.IsAtmos, "cached metadata changed");
        while (cached!.ReadBurst(burst) > 0) hash.AppendData(burst);
        Require(Convert.ToHexString(hash.GetHashAndReset()) == expectedHash, "cached stream differs from FFmpeg oracle");
        Console.WriteLine($"IEC61937 exact match: {count} bursts, Atmos={reader.IsAtmos}, SHA256={actualHash}");
    }

    private static void RunAtmosTests(string root)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "eac3-5.1.m4a");
        Run("Atmos cache: supported files reuse metadata without changing IEC bytes", () =>
        {
            var cache = new AtmosProbeCache();
            using (var first = cache.TryOpen(path)) Require(first?.ProbeCompleted == true, "initial probe missing");
            using var cached = cache.TryOpen(path);
            Require(cached != null && !cached.ProbeCompleted, "cache repeated stream-info probe");
            byte[] burst = new byte[Eac3BitstreamReader.BurstBytes];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (cached!.ReadBurst(burst) > 0) hash.AppendData(burst);
            Require(Convert.ToHexString(hash.GetHashAndReset()) == Eac3OracleHash, "cached metadata changed packet stream");
        });
        Run("Atmos cache: negative results invalidate on length or timestamp changes", () =>
        {
            string copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
            var cache = new AtmosProbeCache();
            try
            {
                File.Copy(Path.Combine(root, "_tools", "test_tone.wav"), copy);
                DateTime stamp = File.GetLastWriteTimeUtc(copy);
                Require(cache.TryOpen(copy) == null && cache.TryOpen(copy) == null, "PCM incorrectly selected");
                File.Copy(path, copy, true);
                File.SetLastWriteTimeUtc(copy, stamp); // Only size changes.
                using (var fresh = cache.TryOpen(copy)) Require(fresh?.ProbeCompleted == true, "length change did not invalidate");
                File.SetLastWriteTimeUtc(copy, stamp.AddSeconds(10)); // Same bytes and size, new timestamp.
                using (var fresh = cache.TryOpen(copy)) Require(fresh?.ProbeCompleted == true, "timestamp change did not invalidate");
                using (var hit = cache.TryOpen(copy)) Require(hit != null && !hit.ProbeCompleted, "new identity not cached");
            }
            finally { File.Delete(copy); }
        });
        Run("Atmos cache: failed opens are not cached as unsupported formats", () =>
        {
            string copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
            var cache = new AtmosProbeCache();
            try
            {
                Require(cache.TryOpen(copy) == null, "missing file opened");
                File.Copy(path, copy);
                using var reader = cache.TryOpen(copy);
                Require(reader != null, "missing-file result poisoned cache");
            }
            finally { File.Delete(copy); }
        });
        Run("Atmos cache: capacity is bounded and eviction keeps recent entries", () =>
        {
            var cache = new AtmosProbeCache();
            string prefix = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string last = "";
            try
            {
                for (int i = 0; i <= AtmosProbeCache.Capacity; i++)
                {
                    string copy = prefix + i + ".m4a";
                    File.Copy(path, copy);
                    try { using var reader = cache.TryOpen(copy); Require(reader != null, "cache fixture failed"); }
                    finally { if (i < AtmosProbeCache.Capacity) File.Delete(copy); else last = copy; }
                }
                var entries = (System.Collections.IDictionary)typeof(AtmosProbeCache).GetField("_entries", Private)!.GetValue(cache)!;
                Require(entries.Count == AtmosProbeCache.Capacity, "cache exceeded capacity");
                using var recent = cache.TryOpen(last);
                Require(recent != null && !recent.ProbeCompleted, "eviction removed recent metadata");
            }
            finally { if (last.Length > 0) File.Delete(last); }
        });
        Run("Atmos transport: distinguish EOF, read errors and incomplete bursts", () =>
        {
            Require(Eac3BitstreamReader.IsEndOfInput(ffmpeg.AVERROR_EOF, 0), "EOF rejected");
            Require(!Eac3BitstreamReader.IsEndOfInput(0, 0), "successful read treated as EOF");
            try { Eac3BitstreamReader.IsEndOfInput(-5, 0); throw new Exception("I/O failure treated as EOF"); }
            catch (IOException) { }
            try { Eac3BitstreamReader.IsEndOfInput(ffmpeg.AVERROR_EOF, 3); throw new Exception("partial burst treated as EOF"); }
            catch (InvalidDataException) { }
        });
        Run("Atmos transport: malformed access unit reports failure and rebuilds PCM at the same position", () =>
        {
            string copy = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m4a");
            Session? pcm = null;
            try
            {
                byte[] file = File.ReadAllBytes(path);
                AVFormatContext* format = null;
                AVPacket* packet = ffmpeg.av_packet_alloc();
                try
                {
                    Require(ffmpeg.avformat_open_input(&format, path, null, null) == 0, "fixture open failed");
                    int audio = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                    int count = 0;
                    long offset = -1;
                    while (ffmpeg.av_read_frame(format, packet) >= 0)
                    {
                        if (packet->stream_index == audio && ++count == 3) offset = packet->pos;
                        ffmpeg.av_packet_unref(packet);
                        if (offset >= 0) break;
                    }
                    Require(offset >= 0 && file[offset] == 0x0B && file[offset + 1] == 0x77, "fixture packet offset missing");
                    file[offset + 2] |= 0x80; // Converted independent stream type is outside this transport's supported grouping.
                }
                finally { ffmpeg.av_packet_free(&packet); ffmpeg.avformat_close_input(&format); }
                File.WriteAllBytes(copy, file);
                var engine = Engine("WasapiExclusiveEvent");
                engine.ExperimentalAtmosPassthrough = true;
                engine.MusicUrl = copy;
                using var failed = (Session?)Invoke(engine, "OpenSession", copy, false, null);
                Require(failed?.Kind == RenderKind.Eac3, "encoded session not selected");
                Require(SpinWait.SpinUntil(() => failed!.DecodeFailure != null, 2000), "decode failure not published");
                Require(!failed!.IsDrained, "failure reported as natural EOF");
                failed.AnchorFrames = failed.MsToFrames(500);
                Set(engine, "_session", failed);
                Require((bool)Invoke(engine, "RebuildBitstreamAsPcm", failed)!, "PCM rebuild failed");
                pcm = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
                Require(pcm.Kind == RenderKind.Pcm && pcm.CurrentMs == 500, "fallback retried Atmos or lost position");
            }
            finally { pcm?.Dispose(); File.Delete(copy); }
        });
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
                Require(session?.Kind == (enabled && mode != "ASIO" ? RenderKind.Eac3 : RenderKind.Pcm), "experimental path escaped scope");
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
            native.Table[7] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, WAVEFORMATEX*, WAVEFORMATEX**, int>)&SupportIecFormat;
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
            var legacy = BinarySerializer.ReadIpcSetting(bytes.AsSpan(0, size - 3));
            Require(!legacy.ExperimentalAtmosPassthrough && legacy.ExperimentalSurround51, "old 5.1 setting changed");
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SupportIecFormat(IntPtr self, int mode, WAVEFORMATEX* format, WAVEFORMATEX** closest) => 0;

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
