using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Services.WebDav;

internal static class WebDavTransitionProbe
{
    public static async Task RunAsync(WebDavTransport transport, string executable, CancellationToken token)
    {
        // Identical PCM formats on both sides isolate the WebDAV eligibility rule.
        await using var origin = new Fixture(wave: SilentWave(2400));
        var connection = new WebDavConnection(new Uri(origin.Root), "", "");
        var entries = new List<WebDavEntry>();
        await foreach (var entry in transport.ListAsync(connection, "/dav/", token))
            if (!entry.IsDirectory) entries.Add(entry);
        var track = entries.Single();
        var local = Path.Combine(Path.GetTempPath(), $"dav-transition-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(local, SilentWave(1600), token);
            await NativePlaybackProbe.WithPlayerAsync(executable, async () =>
            {
                await using var commands = await PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, token);
                await using var states = await PipeStateClient.ConnectAsync(IpcConstants.StatePipeName, token);
                if (commands.InstanceId != states.InstanceId) throw new Exception("Player instance mismatch");
                int localEnds = 0, handoffs = 0;
                states.NotificationReceived += (type, _) =>
                {
                    if (type == MessageTypeId.PlayEnded) Interlocked.Increment(ref localEnds);
                    if (type == MessageTypeId.GaplessTransition) Interlocked.Increment(ref handoffs);
                };
                states.Start();
                await using var streams = new StreamingClient();
                var reply = new byte[IpcConstants.MaxResponseSize];
                var play = new byte[BinarySerializer.PlayRequestSize];
                BinarySerializer.WritePlayRequest(play, new PlayRequest { Url = local });
                byte[] settings = new byte[1024];
                int length = BinarySerializer.WriteIpcSetting(settings, new IpcSetting
                {
                    OutputMode = "WasapiShared", BassOutputDeviceId = -1, Volume = 0.02f, Latency = 100
                });
                await Send(CommandId.UpdateSettings, settings.AsMemory(0, length));
                long epoch = 0;
                foreach (var style in new[] { TrackTransitionStyle.Crossfade, TrackTransitionStyle.Smart })
                {
                    byte[] dsp = new byte[DspProtocol.SettingsSize];
                    DspProtocol.WriteSettings(dsp, new DspSettings { GaplessPlayback = true, TransitionStyle = style });
                    await Send(CommandId.UpdateDsp, dsp);
                    foreach (bool natural in new[] { false, true })
                    {
                        int beforeEnds = Volatile.Read(ref localEnds);
                        int beforeGets = Volatile.Read(ref origin.Gets);
                        await Send(CommandId.Play, play);
                        epoch = await PlayingEpoch(1600, epoch);
                        if (natural)
                            await WaitUntil(() => Volatile.Read(ref localEnds) == beforeEnds + 1, "local end", token);

                        await using var bridge = new WebDavPlaybackBridge(
                            new RemoteReadSession(transport, connection, track, new RemoteAudioCache(), track.Href));
                        bridge.Start();
                        var source = new PlaybackSource
                        {
                            Kind = PlaybackSourceKind.Http, ResourceId = track.Href, Location = bridge.Location,
                            FileExtension = Path.GetExtension(track.Name), ContentLength = track.Length, ETag = track.ETag
                        };
                        var id = Guid.NewGuid();
                        if (!(await streams.PrepareAsync(source, id, token)).Accepted || !(await streams.PlayAsync(id, token)).Accepted)
                            throw new Exception("WebDAV fallback prepare/play rejected");
                        epoch = await PlayingEpoch(2400, epoch);
                        var status = await streams.StatusAsync(id, token);
                        if (status.Phase != StreamPhase.Playing || !status.WantsPlay || status.PositionMs <= 0)
                            throw new Exception($"WebDAV fallback did not render: {status}");
                        bridge.SetPlaying(true);

                        // Exercise the engine guard as well as the host routing tests.
                        var request = new GaplessRequest(epoch, 17, local);
                        var (type, size) = await commands.RequestAsync(CommandId.TransitionNow, request.Write(), reply, token: token);
                        if (type != MessageTypeId.Success || size != 1 || reply[0] != (byte)TransitionNowStatus.Unavailable)
                            throw new Exception("Remote stream admitted a local PCM crossfade");
                        if (natural)
                        {
                            do
                            {
                                await Task.Delay(25, token);
                                status = await streams.StatusAsync(id, token);
                                if (!status.Accepted || status.Phase == StreamPhase.Failed)
                                    throw new Exception($"WebDAV failed before reaching EOF: {status}");
                            } while (status.Phase != StreamPhase.Ended);
                        }

                        if (!(await streams.StopAsync(id, token)).Accepted) throw new Exception("Stop rejected");
                        await Send(CommandId.Play, play);
                        epoch = await PlayingEpoch(1600, epoch);
                        if ((await streams.StatusAsync(id, token)).Accepted || Volatile.Read(ref handoffs) != 0
                            || Volatile.Read(ref localEnds) != beforeEnds + (natural ? 1 : 0)
                            || Volatile.Read(ref origin.Gets) <= beforeGets)
                            throw new Exception("Wrong end count, obsolete stream, missing origin read or spurious gapless handoff");
                        await Send(CommandId.MusicEnd, ReadOnlyMemory<byte>.Empty);
                        Console.WriteLine($"PASS {style} {(natural ? "EOF" : "manual")}: local -> WebDAV -> local, origin GET, distinct playing epochs and correct end notifications.");
                    }
                }

                async Task Send(CommandId command, ReadOnlyMemory<byte> payload)
                {
                    var result = await commands.RequestAsync(command, payload.Span, reply, token: token);
                    if (result.Type != MessageTypeId.Success) throw new IOException($"{command} failed");
                }

                async Task<long> PlayingEpoch(long expectedMs, long previousEpoch)
                {
                    ProgressSnapshot snapshot = default;
                    await WaitUntil(() => states.TryGetProgress(out snapshot) && snapshot.Playing && snapshot.Epoch > previousEpoch
                        && snapshot.GaplessToken == 0 && snapshot.TotalMs == expectedMs && snapshot.CurrentMs > 100,
                        $"rendering {expectedMs} ms track after epoch {previousEpoch}", token);
                    return snapshot.Epoch;
                }
            });
        }
        finally { File.Delete(local); }
    }

    private static async Task WaitUntil(Func<bool> condition, string operation, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + 6000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(operation);
            await Task.Delay(25, token);
        }
    }

    private static byte[] SilentWave(int milliseconds)
    {
        const int rate = 48000, channels = 2, frameBytes = channels * sizeof(short);
        int dataBytes = rate * milliseconds / 1000 * frameBytes;
        byte[] bytes = new byte[44 + dataBytes];
        using var writer = new BinaryWriter(new MemoryStream(bytes));
        writer.Write("RIFF"u8);
        writer.Write(bytes.Length - 8);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * frameBytes);
        writer.Write((short)frameBytes);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        return bytes;
    }
}
