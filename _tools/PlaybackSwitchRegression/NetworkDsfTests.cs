using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services.WebDav;
using FFmpeg.AutoGen;

internal static unsafe partial class Program
{
    private static void RunNetworkDsfTests()
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Run("DSF cover seeks to tail without reading audio", () =>
        {
            using var stream = new VirtualDsf();
            Require(AudioCoverReader.ReadCover(stream, ".dsf").AsSpan().SequenceEqual(VirtualDsf.Picture), "DSF embedded cover missing");
            Require(stream.BytesRead < 4096, "cover read traversed audio");
        });
        Run("DSF native demuxer: exact block alignment, forward/backward, EOF and restart", CheckDsfPacketSeeking);
        foreach (var kind in new[] { RenderKind.Pcm, RenderKind.NativeDsd, RenderKind.Dop })
        {
            Run($"HTTP {kind}: unindexed 15 minute seek uses bounded transfer", () => CheckNetworkDsfSeek(kind));
            Run($"HTTP {kind}: stop interrupts stalled seek", () => CheckDsfCancellation(kind));
        }
        foreach (var mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiShared" })
            Run($"HTTP {mode}: opaque source selects and prepares correct format", () => CheckDsfPreparation(mode));
        Run("HTTP PCM: repeated close releases sessions and native allocations", CheckNetworkDsfMemory);
        Run("Native ring: stop drains copies, rejects late writes and wakes a blocked producer", () =>
        {
            using var ring = new PcmRing(2, 16, 0, -1, nativeStorage: true);
            double[] data = Enumerable.Repeat(0.25, 32).ToArray();
            Require(ring.Push(data, 16, () => false), "initial push failed");
            var producer = Task.Run(() => ring.Push(data, 16, () => false));
            Require(!producer.Wait(100), "full ring must block its producer");
            ring.Dispose(); ring.Dispose();
            Require(producer.Wait(1000) && !producer.Result, "blocked producer not released");
            Require(ring.Render(data, 16) == 0 && data.All(x => x == 0), "disposed ring rendered stale data");
        });
        Run("DSF cover: malformed offset and cover budget are rejected", () =>
        {
            byte[] header = new byte[28]; "DSD "u8.CopyTo(header);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), 28);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20), ulong.MaxValue);
            using var invalid = new MemoryStream(header);
            Require(AudioCoverReader.ReadCover(invalid, ".dsf").Length == 0, "invalid offset accepted");
            using var limited = new VirtualDsf();
            Require(AudioCoverReader.ReadCover(limited, ".dsf", 1).Length == 0, "cover budget ignored");
        });
        Run("ASIO: closing message windows releases their source sessions", () =>
        {
            var refs = new List<WeakReference>();
            for (int i = 0; i < 20; i++) refs.Add(OpenCloseAsioWindow());
            Thread.Sleep(200);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(refs.All(r => !r.IsAlive), "ASIO message window retains closed output");
        });
    }

    private static void CheckDsfPacketSeeking()
    {
        using var server = new DsfHttpFixture();
        AVFormatContext* format = null;
        AVPacket* packet = null;
        try
        {
            Require(ffmpeg.avformat_open_input(&format, server.Url, null, null) >= 0, "demux open failed");
            packet = ffmpeg.av_packet_alloc();
            foreach (var (timestamp, flags) in new[] { (317520123L, ffmpeg.AVSEEK_FLAG_BACKWARD), (4097L, 0), (8191L, ffmpeg.AVSEEK_FLAG_BACKWARD), (0L, 0) })
            {
                Require(ffmpeg.av_seek_frame(format, 0, timestamp, flags) >= 0, "block seek failed");
                int result;
                do { ffmpeg.av_packet_unref(packet); result = ffmpeg.av_read_frame(format, packet); }
                while (result >= 0 && packet->stream_index != 0);
                long aligned = (flags & ffmpeg.AVSEEK_FLAG_BACKWARD) != 0 ? timestamp / 4096 * 4096 : (timestamp + 4095) / 4096 * 4096;
                Require(result >= 0 && packet->pts == aligned && packet->size == 8192, "seek returned wrong packet or timestamp");
            }
            Require(ffmpeg.av_seek_frame(format, 0, format->streams[0]->duration, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0, "EOF seek failed");
            int end;
            do { ffmpeg.av_packet_unref(packet); end = ffmpeg.av_read_frame(format, packet); }
            while (end >= 0 && packet->stream_index != 0);
            Require(end == ffmpeg.AVERROR_EOF, "EOF seek read audio/tag as audio");
            Require(ffmpeg.av_seek_frame(format, 0, 0, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0, "restart seek failed");
            int first;
            do { ffmpeg.av_packet_unref(packet); first = ffmpeg.av_read_frame(format, packet); }
            while (first >= 0 && packet->stream_index != 0);
            Require(first >= 0 && packet->pts == 0, "restart after EOF failed");
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static void CheckDsfCancellation(RenderKind kind)
    {
        using var server = new DsfHttpFixture();
        using var session = Session.Open(null!, server.Url, kind, 176400, 0, 100, source: DsfSource(server))
            ?? throw new Exception("open failed");
        Require(SpinWait.SpinUntil(() => session.ReadyFrames > 0, 4000), "no data");
        server.Stall = true;
        session.RequestSeek(900000, 1);
        Require(server.Blocked.Wait(3000), "seek did not reach stalled server");
        var clock = Stopwatch.StartNew();
        session.Dispose();
        var thread = (Thread)typeof(Session).GetField("_thread", Private)!.GetValue(session)!;
        Require(clock.ElapsedMilliseconds < 1500 && !thread.IsAlive, "decoder thread survived cancellation");
    }

    private static void CheckDsfPreparation(string mode)
    {
        using var server = new DsfHttpFixture();
        var engine = Engine(mode);
        Set(engine, "_streamLock", new object());
        Guid id = Guid.NewGuid();
        var source = DsfSource(server) with { Location = server.Url.Replace("audio.dsf", "audio"), FileExtension = ".dsf" };
        try
        {
            Require(engine.HandleStreamCommand(new() { Method = "prepare", SessionId = id, Source = source }).Accepted, "prepare rejected");
            StreamReply state = new();
            Require(SpinWait.SpinUntil(() =>
            {
                state = engine.HandleStreamCommand(new() { Method = "status", SessionId = id });
                return state.Phase is StreamPhase.Ready or StreamPhase.Failed;
            }, 5000) && state.Phase == StreamPhase.Ready && state.BufferedMs >= 100, "prepare did not buffer");
            var slots = (System.Collections.IDictionary)typeof(PlaybackEngine).GetField("_streamSlots", Private)!.GetValue(engine)!;
            var slot = slots[id]!;
            var session = (Session)slot.GetType().GetField("Prepared")!.GetValue(slot)!;
            Require(session.Kind == (mode == "ASIO" ? RenderKind.NativeDsd : mode == "WasapiShared" ? RenderKind.Pcm : RenderKind.Dop), "wrong prepared render kind");
            Set(engine, "_currentStream", id);
            using var fallback = (Session?)Invoke(engine, "OpenSession", source.Location, false, RenderKind.Dop);
            Require(fallback?.Kind == RenderKind.Dop, "remote Native -> DoP fallback forced PCM");
        }
        finally
        {
            Set(engine, "_currentStream", Guid.Empty);
            engine.StopStreamingAsync().GetAwaiter().GetResult();
        }
    }

    private static void CheckRealAsioNetworkMemory()
    {
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        var drivers = AudioPlayer.Interop.Win32.EnumerateAsioDrivers();
        int device = drivers.FindIndex(d => d.Name == "FiiO ASIO Driver");
        Require(device >= 0, "FiiO ASIO Driver unavailable");
        using var server = new DsfHttpFixture();
        long baseline = 0;
        var refs = new List<WeakReference>();
        for (int i = 0; i < 15; i++)
        {
            refs.Add(PlayCloseDsf(server, device));
            Thread.Sleep(100);
            using var process = Process.GetCurrentProcess();
            long bytes = process.PrivateMemorySize64;
            if (i == 2) baseline = bytes;
            Console.WriteLine($"ASIO switch {i + 1}: private={bytes}, managed={GC.GetTotalMemory(false)}, threads={process.Threads.Count}");
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"ASIO after GC: private={Process.GetCurrentProcess().PrivateMemorySize64}, live={refs.Count(r => r.IsAlive)}");
        Require(refs.All(r => !r.IsAlive), "ASIO retained disposed session");
        Require(Process.GetCurrentProcess().PrivateMemorySize64 - baseline < 128L * 1024 * 1024, "ASIO native allocation growth");
        using var dsd256 = new DsfHttpFixture(dsdRate: 11289600);
        PlayCloseDsf(dsd256, device, RenderKind.NativeDsd);
        Console.WriteLine("ASIO DSD256 NativeDsd: real driver rendered network bitstream");
        PlayCloseDsf(server, device, RenderKind.Dop);
        Console.WriteLine("ASIO DSD64 Dop: real driver rendered network bitstream");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PlayCloseDsf(DsfHttpFixture server, int device, RenderKind kind = RenderKind.Pcm)
    {
        using var session = Session.Open(null!, server.Url, kind, 176400, 0, 100, source: DsfSource(server))
            ?? throw new Exception("DSF open failed");
        Require(SpinWait.SpinUntil(() => session.ReadyFrames >= session.InitialBufferFrames, 4000), "prepare failed");
        using var output = new AudioPlayer.Interop.AsioOutput();
        Require(output.Start(device, session), "ASIO start failed");
        Thread.Sleep(200);
        Require(session.FramesPlayed > 0, "ASIO did not render");
        return new WeakReference(session);
    }

    private static void CheckNasDsf()
    {
        string address = Environment.GetEnvironmentVariable("MUSIC_WEBDAV_URL") ?? throw new Exception("MUSIC_WEBDAV_URL required");
        string href = Environment.GetEnvironmentVariable("MUSIC_WEBDAV_DSF") ?? throw new Exception("MUSIC_WEBDAV_DSF required");
        using var transport = new WebDavTransport();
        var connection = new WebDavConnection(new Uri(address), Environment.GetEnvironmentVariable("MUSIC_WEBDAV_USER") ?? "", Environment.GetEnvironmentVariable("MUSIC_WEBDAV_PASSWORD") ?? "");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var response = transport.OpenAsync(connection, href, 0, 91, true, deadline.Token).GetAwaiter().GetResult();
        long length = response.Message.Content.Headers.ContentRange!.Length!.Value;
        var entry = new WebDavEntry(href, "fixture.dsf", false, length, response.Message.Headers.ETag?.ToString() ?? "", response.Message.Content.Headers.LastModified);
        response.DisposeAsync().AsTask().GetAwaiter().GetResult();
        using (var coverStream = new HttpRangeReadStream(transport, connection, entry, deadline.Token, 8 * 1024 * 1024, 64))
        {
            var cover = AudioCoverReader.ReadCover(coverStream, ".dsf");
            Console.WriteLine($"NAS DSF length={length}, cover={cover.Length} bytes, cover transfer={coverStream.DownloadedBytes}");
            Require(cover.Length > 0, "NAS DSF cover missing");
        }
        var reads = new RemoteReadSession(transport, connection, entry, new RemoteAudioCache(), "regression");
        var bridge = new WebDavPlaybackBridge(reads);
        try
        {
            bridge.Start();
            foreach (var kind in new[] { RenderKind.Pcm, RenderKind.NativeDsd, RenderKind.Dop })
            {
                using var session = Session.Open(null!, bridge.Location, kind, 176400, 0, 100,
                    source: new() { Kind = PlaybackSourceKind.Http, Location = bridge.Location, FileExtension = ".dsf" }, cancellationToken: deadline.Token)
                    ?? throw new Exception("NAS DSF open failed");
                Require(SpinWait.SpinUntil(() => session.ReadyFrames >= session.InitialBufferFrames || session.DecodeFailure != null, 20000), "NAS prebuffer timeout");
                Require(session.DecodeFailure == null, "NAS prebuffer failed");
                long before = reads.NetworkBytes;
                var clock = Stopwatch.StartNew();
                session.RequestSeek(session.TotalMs * 4 / 5, 1);
                Require(SpinWait.SpinUntil(() => session.CompletedSeekId == 1 || session.DecodeFailure != null, 10000) && session.DecodeFailure == null, "NAS seek failed");
                Require(SpinWait.SpinUntil(() => session.ReadyFrames >= session.InitialBufferFrames || session.DecodeFailure != null, 15000), "NAS seek prebuffer timeout");
                long transferred = reads.NetworkBytes - before;
                Console.WriteLine($"NAS {kind}: duration={session.TotalMs}, seek+prebuffer={clock.ElapsedMilliseconds} ms, transfer={transferred}");
                Require(transferred < 64 * 1024 * 1024, "NAS seek scanned intermediate audio");
            }
        }
        finally { bridge.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenCloseAsioWindow()
    {
        using var output = new AudioPlayer.Interop.AsioOutput();
        Require((bool)Invoke(output, "StartWindowThread")!, "window failed");
        return new WeakReference(output);
    }

    private static PlaybackSource DsfSource(DsfHttpFixture server) => new()
    {
        Kind = PlaybackSourceKind.Http, Location = server.Url, ResourceId = "fixture.dsf",
        Buffer = new() { InitialMs = 100, ResumeMs = 200, CapacityMs = 8000, OpenTimeoutMs = 4000, ReadTimeoutMs = 1000, RetryCount = 0 }
    };

    private static void CheckNetworkDsfSeek(RenderKind kind)
    {
        using var server = new DsfHttpFixture(32L * 1024 * 1024);
        using var session = Session.Open(null!, server.Url, kind, 176400, 0, 100, source: DsfSource(server)) ?? throw new Exception("open failed");
        Require(SpinWait.SpinUntil(() => session.ReadyFrames > 0 || session.DecodeFailure != null, 5000), "no initial data");
        long before = server.BytesSent;
        var timer = Stopwatch.StartNew();
        session.RequestSeek(900000, 1);
        bool completed = SpinWait.SpinUntil(() => session.CompletedSeekId == 1 || session.DecodeFailure != null, 5000);
        long bytes = server.BytesSent - before;
        Console.WriteLine($"DSF {kind}: seek {timer.ElapsedMilliseconds} ms, sent {bytes} bytes, error {session.DecodeFailure?.Message}");
        Require(completed && session.CompletedSeekId == 1 && session.DecodeFailure == null, "seek failed/scanned skipped audio");
        Require(bytes < 16 * 1024 * 1024, "seek transferred skipped audio");
        Require(SpinWait.SpinUntil(() => session.ReadyFrames > 0, 2000), "no data after seek");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference OpenCloseDsf(DsfHttpFixture server)
    {
        using var session = Session.Open(null!, server.Url, RenderKind.Pcm, 176400, 0, 100, source: DsfSource(server)) ?? throw new Exception("open failed");
        Require(SpinWait.SpinUntil(() => session.ReadyFrames >= session.InitialBufferFrames || session.DecodeFailure != null, 4000), "prepare timed out");
        Require(session.DecodeFailure == null, "decode failed");
        return new WeakReference(session);
    }

    private static void CheckNetworkDsfMemory()
    {
        using var server = new DsfHttpFixture();
        for (int i = 0; i < 3; i++) OpenCloseDsf(server);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); // Measurement boundary, never production policy.
        long managedBefore = GC.GetTotalMemory(false);
        long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var references = new List<WeakReference>();
        for (int i = 0; i < 30; i++) references.Add(OpenCloseDsf(server));
        long uncollected = GC.GetTotalMemory(false);
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long managedAfter = GC.GetTotalMemory(false);
        long privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;
        Console.WriteLine($"DSF 30 switches: allocated={allocated}, managed before={managedBefore}, before GC={uncollected}, after={managedAfter}; private before={privateBefore}, after={privateAfter}; live={references.Count(r => r.IsAlive)}");
        Require(references.All(r => !r.IsAlive), "closed session still rooted");
        Require(managedAfter - managedBefore < 8 * 1024 * 1024, "managed retention grows across switches");
        Require(privateAfter - privateBefore < 128 * 1024 * 1024, "native/private memory grows across switches");
        Require(allocated < 64 * 1024 * 1024, "switches allocate large audio rings on the managed heap");
    }
}
