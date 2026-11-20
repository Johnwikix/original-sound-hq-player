using System.Buffers.Binary;
using System.IO.Pipes;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunPipeProtocolTests()
    {
        Run("Pipe: partial/adjacent frames and malformed input", () => PipeChecks.FramingAsync().GetAwaiter().GetResult());
        Run("Pipe: legacy next-track plans and Smart boundary flag", PipeChecks.NextTrackRequest);
        Run("Pipe: legacy state readers retain the committed 72-byte progress during overlap", () => PipeChecks.LegacyTransitionProgressAsync().GetAwaiter().GetResult());
        Run("Pipe: real commands and PCM mixing publish preview, supersession and final commit in order", CheckTransitionProgressPipe);
        Run("Pipe: abandoned replies cannot overwrite released buffers", () => PipeChecks.AbandonedRepliesAsync().GetAwaiter().GetResult());
        Run("Pipe: disconnect completes all outstanding requests", () => PipeChecks.DisconnectAsync().GetAwaiter().GetResult());
        Run("Pipe: queue capacity and in-flight disposal", () => PipeChecks.QueueAndShutdownAsync().GetAwaiter().GetResult());
        Run("Pipe: 256 KiB configuration is one acknowledged command", () => PipeChecks.LargePayloadAsync().GetAwaiter().GetResult());
        Run("Pipe: invalid handshake permits subsequent connection", () => PipeChecks.HandshakeAsync().GetAwaiter().GetResult());
        Run("Pipe: stalled subscriber retains final state and every critical event", () => PipeChecks.StateBackpressureAsync().GetAwaiter().GetResult());
        Run("Pipe: critical event overflow fails explicitly", () => PipeChecks.StateOverflowAsync().GetAwaiter().GetResult());
        Run("Pipe: connection cancellation and stop before listeners start", () => PipeChecks.StartupShutdownAsync().GetAwaiter().GetResult());
    }

    private static void CheckTransitionProgressPipe()
    {
        FFmpeg.AutoGen.ffmpeg.RootPath = AppContext.BaseDirectory;
        string path = Path.Combine(AppContext.BaseDirectory, "transition-progress-pipe.wav");
        WriteLoudnessTone(path, .2f);
        using var stop = new CancellationTokenSource(15000);
        var (engine, source, first) = OpenManualFixture();
        using var player = new AudioPlayer.PlayerIpcService();
        Set(player, "_engine", engine);
        Set(engine, "_ipc", player);
        var publisher = (PipeStateServer)typeof(AudioPlayer.PlayerIpcService).GetField("_stateServer", Private)!.GetValue(player)!;
        string commandName = "TransitionCommands-" + Guid.NewGuid().ToString("N");
        string stateName = "TransitionState-" + Guid.NewGuid().ToString("N");
        Guid instance = Guid.NewGuid();
        Task commandServer = new PipeCommandServer(commandName, instance, player.HandleCommand).RunAsync(stop.Token);
        Task stateServer = publisher.RunAsync(stateName, instance, stop.Token);
        using var commands = PipeCommandClient.ConnectAsync(commandName, stop.Token).GetAwaiter().GetResult();
        using var client = PipeStateClient.ConnectAsync(stateName, stop.Token).GetAwaiter().GetResult();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<(long ActiveToken, long CommitToken)>();
        client.TransitionProgressChanged += () =>
        {
            if (client.TryGetProgress(out var p)) changes.Enqueue((p.TransitionToken, p.GaplessToken));
        };
        client.Start();
        try
        {
            byte[] response = new byte[IpcConstants.MaxResponseSize];
            byte[] cancel = new GaplessRequest(0, 0, "").Write();
            double[] buffer = new double[480 * 2];
            for (long token = 101; token <= 103; token++)
            {
                var plan = new GaplessRequest(first.TimelineEpoch, token, path);
                var reply = commands.RequestAsync(CommandId.TransitionNow, plan.Write(), response, token: stop.Token).GetAwaiter().GetResult();
                Require(reply is { Type: MessageTypeId.Success, Length: 1 } && response[0] == (byte)TransitionNowStatus.Preparing,
                    "Manual playback command did not reserve the requested token");
                var next = WaitManualReady(engine, source);
                Publish(0);
                for (int i = 0; i < 4; i++) source.FillPcm(buffer, 480); // Also lets the previous 20ms withdrawal finish.
                var active = Publish(token);
                Require(active.Epoch == first.TimelineEpoch && active.GaplessToken == 0 && active.TransitionEpoch == next.TimelineEpoch
                    && active.TransitionCurrentMs > 0, "Active state did not retain committed identity or advance the incoming position");
                source.FillPcm(buffer, 480);
                Require(Publish(token).TransitionCurrentMs > active.TransitionCurrentMs, "Incoming progress did not advance on the wire");
                if (token < 103)
                {
                    reply = commands.RequestAsync(CommandId.QueueNext, cancel, response, token: stop.Token).GetAwaiter().GetResult();
                    Require(reply is { Type: MessageTypeId.Success, Length: 16 } && BinaryPrimitives.ReadInt64LittleEndian(response) == 0,
                        "Ordered cancellation committed a partial overlap");
                    Require(Publish(0).Epoch == first.TimelineEpoch, "Cancellation retained the incoming timeline");
                }
                else
                {
                    Require(SpinWait.SpinUntil(() => { source.FillPcm(buffer, 480); return source.CompletedToken == token; }, 5000),
                        "Last PCM attempt did not complete");
                    var committed = Publish(0);
                    Require(committed.GaplessToken == token && committed.Epoch == active.TransitionEpoch,
                        "Commit did not adopt the exact track previously presented");
                }
            }
            (long, long)[] expected = [(101, 0), (0, 0), (102, 0), (0, 0), (103, 0), (0, 103)];
            Require(SpinWait.SpinUntil(() => changes.Count >= expected.Length, 5000) && changes.SequenceEqual(expected),
                "Incoming status changes were lost, duplicated during progress, or restored a superseded timeline");

            ProgressSnapshot Publish(long expectedToken)
            {
                long revision = client.TryGetProgress(out var previous) ? previous.Revision : 0;
                Require(engine.TryCaptureProgress(out var captured), "Engine progress capture unavailable");
                publisher.PublishProgress(captured);
                Require(SpinWait.SpinUntil(() => client.TryGetProgress(out var updated) && updated.Revision > revision, 5000),
                    "Progress did not arrive in the state pipe");
                Require(client.TryGetProgress(out var delivered) && delivered.TransitionToken == expectedToken, "Unexpected preview token on the wire");
                return delivered;
            }
        }
        finally
        {
            stop.Cancel();
            try { Task.WhenAll(commandServer, stateServer).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            finally
            {
                Set(player, "_engine", null);
                CloseManualFixture(engine);
            }
        }
    }
}

internal static class PipeChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Name() => "PipeRegression-" + Guid.NewGuid().ToString("N");
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(5000);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token);
    }
    public static async Task FramingAsync()
    {
        using var bytes = new MemoryStream();
        var writer = new PipeFrameWriter();
        byte[] payload = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        await writer.WriteAsync(bytes, PipeFrameKind.Request, 123, 7, payload, default);
        await writer.WriteAsync(bytes, PipeFrameKind.Response, 124, 2, ReadOnlyMemory<byte>.Empty, default);
        byte[] valid = bytes.ToArray();
        using var fragmented = new FragmentedStream(valid);
        using var reader = new PipeFrameReader();
        var first = await reader.ReadAsync(fragmented, default);
        Check(first.Id == 123 && first.Payload.Span.SequenceEqual(payload), "fragmented payload lost");
        var second = await reader.ReadAsync(fragmented, default);
        Check(second.Id == 124 && second.Kind == PipeFrameKind.Response && second.Payload.IsEmpty, "adjacent frame lost");
        foreach (int length in new[] { -1, IpcConstants.MaxPayloadSize + 1 })
        {
            byte[] header = valid[..PipeProtocol.HeaderSize];
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), length);
            await ExpectAsync<InvalidDataException>(async () => await reader.ReadAsync(new MemoryStream(header), default));
        }
        byte[] wrongVersion = valid[..PipeProtocol.HeaderSize];
        wrongVersion[4]++;
        await ExpectAsync<InvalidDataException>(async () => await reader.ReadAsync(new MemoryStream(wrongVersion), default));
        await ExpectAsync<EndOfStreamException>(async () => await reader.ReadAsync(new FragmentedStream(valid[..30]), default));
    }

    public static void NextTrackRequest()
    {
        var legacy = new GaplessRequest(42, 7, @"C:\music\曲.wav");
        byte[] payload = legacy.Write();
        Check(BinaryPrimitives.ReadInt64LittleEndian(payload) == 42 && BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8)) == 7
            && payload.AsSpan(16).SequenceEqual(System.Text.Encoding.UTF8.GetBytes(legacy.Path)) && GaplessRequest.Read(payload) == legacy,
            "legacy epoch/token/path encoding changed");
        var cancel = new GaplessRequest(0, 0, "");
        Check(cancel.Write().AsSpan().SequenceEqual(new byte[16]) && GaplessRequest.Read(cancel.Write()) == cancel,
            "legacy cancellation encoding changed");

        var protectedTrack = new GaplessRequest(long.MaxValue, long.MaxValue, new string('x', 2032), true);
        byte[] marked = protectedTrack.Write();
        Check(marked.Length == 2048 && BinaryPrimitives.ReadInt64LittleEndian(marked.AsSpan(8)) == -1
            && GaplessRequest.Read(marked) == protectedTrack, "Smart hint truncated a valid token or maximum-length path");

        byte[] malformed = (byte[])marked.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(8), long.MinValue);
        Reject(() => GaplessRequest.Read(malformed));
        BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(8), -1);
        Reject(() => GaplessRequest.Read(malformed.AsSpan(0, 16)));
        BinaryPrimitives.WriteInt64LittleEndian(malformed, 0);
        Reject(() => GaplessRequest.Read(malformed));
        marked[16] = 0;
        Reject(() => GaplessRequest.Read(marked));
        marked[16] = 0xff;
        Reject(() => GaplessRequest.Read(marked));
        Reject(() => (legacy with { Token = -1 }).Write());
        Reject(() => (protectedTrack with { Path = protectedTrack.Path + "x" }).Write());
        Reject(() => (cancel with { PreserveAlbumBoundary = true }).Write());

        static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("invalid queue plan was accepted");
        }
    }

    public static async Task LegacyTransitionProgressAsync()
    {
        string name = Name();
        using var stop = new CancellationTokenSource(5000);
        using var publisher = new PipeStateServer();
        var server = publisher.RunAsync(name, Guid.NewGuid(), stop.Token);
        using var oldClient = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await PipeProtocol.ConnectAsync(oldClient, stop.Token);
        using var reader = new PipeFrameReader(IpcConstants.MaxStatePayloadSize);
        try
        {
            var snapshot = new ProgressSnapshot(0, 100, 1234, 5000, 1000, true, TransitionToken: 33, TransitionEpoch: 101,
                TransitionCurrentMs: 120, TransitionTotalMs: 9000, TransitionPlaybackRate: 1.5);
            publisher.PublishProgress(snapshot);
            var baseFrame = await reader.ReadAsync(oldClient, stop.Token);
            Check(baseFrame.Kind == PipeFrameKind.Progress && baseFrame.Payload.Length == 72, "Legacy progress length changed at overlap");
            var legacy = ProgressProtocol.Read(baseFrame.Payload.Span);
            Check(legacy.Epoch == 100 && legacy.CurrentMs == 1234 && legacy.GaplessToken == 0 && legacy.TransitionToken == 0,
                "Legacy progress exposed an uncommitted track");
            var companion = await reader.ReadAsync(oldClient, stop.Token);
            Check(companion.Kind == PipeFrameKind.Notification && companion.Id == legacy.Revision
                && companion.Type == (int)MessageTypeId.TransitionProgress && ProgressProtocol.Read(companion.Payload.Span) == (snapshot with { Revision = 1 }),
                "Supplemental progress no longer fits backward-compatible notification framing");
        }
        finally
        {
            stop.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }
    }
    public static async Task AbandonedRepliesAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource(15000);
        int count = 0;
        string name = Name();
        var server = new PipeCommandServer(name, Guid.NewGuid(), (_, _) =>
        {
            if (Interlocked.Increment(ref count) <= 2) { entered.Set(); release.Wait(stop.Token); }
            return new(MessageTypeId.Success, new byte[] { 1, 2, 3, 4 });
        }).RunAsync(stop.Token);
        await using var client = await PipeCommandClient.ConnectAsync(name, stop.Token);
        try
        {
            byte[] buffer = new byte[4];
            var timed = client.RequestAsync(CommandId.PlayButton, [], buffer, timeoutMs: 30);
            await UntilAsync(() => entered.IsSet);
            Check((await timed).Type == MessageTypeId.Failed, "timeout missing");
            buffer.AsSpan().Fill(99);
            release.Set();
            Check((await client.RequestAsync(CommandId.GetDspState, [], new byte[4])).Type == MessageTypeId.Success, "barrier failed");
            Check(buffer.All(x => x == 99), "late timed-out reply modified caller storage");
            entered.Reset(); release.Reset(); Interlocked.Exchange(ref count, 0);
            using var cancelled = new CancellationTokenSource();
            var first = client.RequestAsync(CommandId.PlayButton, [], buffer, token: cancelled.Token);
            await UntilAsync(() => entered.IsSet);
            using var queuedCancellation = new CancellationTokenSource();
            var queued = client.RequestAsync(CommandId.MusicEnd, [], [], token: queuedCancellation.Token);
            queuedCancellation.Cancel(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(async () => await first);
            await ExpectAsync<OperationCanceledException>(async () => await queued);
            buffer.AsSpan().Fill(77);
            var next = client.RequestAsync(CommandId.PlayButton, [], new byte[4]);
            Check(Volatile.Read(ref count) == 1, "cancellation advanced past running command");
            release.Set();
            Check((await next).Type == MessageTypeId.Success && count == 2, "queued cancellation lost later command");
            Check(buffer.All(x => x == 77), "late cancelled reply modified caller storage");
        }
        finally { release.Set(); stop.Cancel(); await client.DisposeAsync(); await server; }
    }
    public static async Task DisconnectAsync()
    {
        string name = Name();
        using var timeout = new CancellationTokenSource(5000);
        var server = Task.Run(async () =>
        {
            using var pipe = PipeProtocol.CreateServer(name);
            await PipeProtocol.AcceptAsync(pipe, Guid.NewGuid(), timeout.Token);
            using var reader = new PipeFrameReader();
            await reader.ReadAsync(pipe, timeout.Token);
        });
        await using var client = await PipeCommandClient.ConnectAsync(name, timeout.Token);
        var requests = Enumerable.Range(0, 20).Select(_ => client.RequestAsync(CommandId.PlayButton, [], [])).ToArray();
        var replies = await Task.WhenAll(requests).WaitAsync(timeout.Token);
        Check(replies.All(x => x.Type == MessageTypeId.Failed), "disconnect left requests unresolved");
        Check(!client.IsConnected && !client.Publish(CommandId.Play, []), "disconnected client accepted a command");
        await server;
    }
    public static async Task QueueAndShutdownAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource(15000);
        string name = Name();
        var server = new PipeCommandServer(name, Guid.NewGuid(), (_, _) =>
        {
            entered.Set(); release.Wait(stop.Token);
            return new(MessageTypeId.Success, ReadOnlyMemory<byte>.Empty);
        }).RunAsync(stop.Token);
        await using var client = await PipeCommandClient.ConnectAsync(name, stop.Token);
        try
        {
            var pending = client.RequestAsync(CommandId.PlayButton, [], []);
            await UntilAsync(() => entered.IsSet);
            for (int i = 0; i < 512; i++) Check(client.Publish(CommandId.PlayButton, []), "queue limit smaller than expected");
            Check(!client.Publish(CommandId.PlayButton, []), "queue is unbounded");
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check((await pending).Type == MessageTypeId.Failed, "disposal did not complete pending call");
        }
        finally { release.Set(); stop.Cancel(); await server; }
    }
    public static async Task LargePayloadAsync()
    {
        using var stop = new CancellationTokenSource(5000);
        string name = Name();
        byte[] payload = new byte[IpcConstants.MaxPayloadSize];
        Random.Shared.NextBytes(payload);
        var server = new PipeCommandServer(name, Guid.NewGuid(), (command, data) =>
        {
            Check(command == CommandId.UpdateDeviceCorrections && data.SequenceEqual(payload), "large configuration torn");
            return new(MessageTypeId.Success, ReadOnlyMemory<byte>.Empty);
        }).RunAsync(stop.Token);
        await using var client = await PipeCommandClient.ConnectAsync(name, stop.Token);
        try { Check((await client.RequestAsync(CommandId.UpdateDeviceCorrections, payload, [])).Type == MessageTypeId.Success, "large command failed"); }
        finally { stop.Cancel(); await client.DisposeAsync(); await server; }
    }
    public static async Task HandshakeAsync()
    {
        using var stop = new CancellationTokenSource(5000);
        string name = Name();
        Guid instance = Guid.NewGuid();
        var server = new PipeCommandServer(name, instance, (_, _) => new(MessageTypeId.Success, ReadOnlyMemory<byte>.Empty)).RunAsync(stop.Token);
        try
        {
            using (var invalid = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await invalid.ConnectAsync(stop.Token);
                await invalid.WriteAsync(new byte[PipeProtocol.HeaderSize], stop.Token);
                Check(await invalid.ReadAsync(new byte[1], stop.Token) == 0, "bad version remained connected");
            }
            await using var client = await PipeCommandClient.ConnectAsync(name, stop.Token);
            Check(client.InstanceId == instance, "instance handshake lost");
            Check((await client.RequestAsync(CommandId.Play, [], [])).Type == MessageTypeId.Success, "failed handshake poisoned server");
        }
        finally { stop.Cancel(); await server; }
    }
    public static async Task StateBackpressureAsync()
    {
        string name = Name();
        using var stop = new CancellationTokenSource(10000);
        using var publisher = new PipeStateServer();
        var server = publisher.RunAsync(name, Guid.NewGuid(), stop.Token);
        await using var client = await PipeStateClient.ConnectAsync(name, stop.Token);
        var events = new List<int>();
        client.NotificationReceived += (_, bytes) => { lock (events) events.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.Span)); };
        try
        {
            for (int i = 1; i <= 20000; i++)
            {
                publisher.PublishProgress(new(0, 3, i, i * 2, i * 3, true, i * 4));
                publisher.PublishDsp(new DspState(0, false, 2, LoudnessStatus.Off, i, 0));
                if (i % 400 == 0)
                {
                    byte[] payload = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, i / 400);
                    publisher.PublishNotification(MessageTypeId.PlayEnded, payload);
                }
            }
            client.Start();
            await UntilAsync(() => client.TryGetProgress(out var value) && value.CurrentMs == 20000
                && client.CurrentDspState?.Revision == 20000 && CountEvents() == 50);
            lock (events) Check(events.SequenceEqual(Enumerable.Range(1, 50)), "critical event order changed");
            Check(client.TryGetProgress(out var snapshot) && snapshot.TotalMs == 40000 && snapshot.SeekId == 80000, "snapshot torn");
        }
        finally
        {
            stop.Cancel(); await client.DisposeAsync();
            try { await server; } catch (OperationCanceledException) { }
        }
        int CountEvents() { lock (events) return events.Count; }
    }
    public static async Task StateOverflowAsync()
    {
        string name = Name();
        using var stop = new CancellationTokenSource(5000);
        using var publisher = new PipeStateServer();
        var server = publisher.RunAsync(name, Guid.NewGuid(), stop.Token);
        await using var client = await PipeStateClient.ConnectAsync(name, stop.Token);
        // Keep the subscriber connected without reading; overflow must interrupt a blocked pipe write.
        for (int i = 0; i < 4096; i++) publisher.PublishNotification(MessageTypeId.PlayEnded, []);
        await ExpectAsync<IOException>(async () => await server.WaitAsync(stop.Token));
    }

    public static async Task StartupShutdownAsync()
    {
        using (var cancelled = new CancellationTokenSource(30))
            await ExpectAsync<OperationCanceledException>(async () => await PipeCommandClient.ConnectAsync(Name(), cancelled.Token));
        using (var cancelled = new CancellationTokenSource(30))
            await ExpectAsync<OperationCanceledException>(async () => await PipeStateClient.ConnectAsync(Name(), cancelled.Token));
        using var timeout = new CancellationTokenSource(5000);
        using var publisher = new PipeStateServer();
        string name = Name();
        var server = publisher.RunAsync(name, Guid.NewGuid(), timeout.Token);
        await using (var client = await PipeStateClient.ConnectAsync(name, timeout.Token))
        {
            // Initialization may be cancelled after the handshake but before Start subscribes.
            await client.DisposeAsync();
            await client.DisposeAsync();
        }
        await ExpectAsync<IOException>(async () => await server.WaitAsync(timeout.Token));
        var player = new AudioPlayer.PlayerIpcService();
        player.Dispose();
        player.Stop();
        player.Dispose();
        await ExpectAsync<ObjectDisposedException>(() => player.StartAsync());
    }
}
