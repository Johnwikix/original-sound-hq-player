using System.Diagnostics;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

internal static class LivePlaybackProbe
{
    private static readonly string IpcScope = "catalog-playback-" + Guid.NewGuid().ToString("N");

    public static async Task RunAsync(IRemoteMediaProvider provider, CatalogSession connection, CatalogTrack track, CancellationToken token)
    {
        string? playerPath = Environment.GetEnvironmentVariable("REMOTE_CATALOG_PLAYER_PATH");
        if (string.IsNullOrWhiteSpace(playerPath)) return;
        string executable = Path.GetFullPath(playerPath);
        Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", IpcScope);

        using var ownerStop = new ManualResetEventSlim();
        var ownerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() => OwnClientMutex(ownerReady, ownerStop)) { IsBackground = true };
        owner.Start();

        Process? player = null;
        PipeCommandClient? control = null;
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            await ownerReady.Task.WaitAsync(token);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("REMOTE_CATALOG_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            player = Process.Start(start) ?? throw new InvalidOperationException("AudioPlayer did not start.");
            output = player.StandardOutput.ReadToEndAsync();
            errors = player.StandardError.ReadToEndAsync();

            string identity = "remote://1/track/" + Uri.EscapeDataString(track.Id);
            var entry = new WebDavEntry(track.Id, track.Title, false, track.Length, "", null);
            var readSession = await RemoteReadSession.CreateAsync(
                (rangeStart, rangeEnd, foreground, cancellation) => provider.OpenAudioAsync(connection, track.Id, rangeStart, rangeEnd, foreground, cancellation),
                entry, new RemoteAudioCache(), identity, probeRange: true, token);
            await using var bridge = new WebDavPlaybackBridge(readSession);
            bridge.Start();
            await using var client = new StreamingClient();
            Check((await client.SendAsync(new(), token)).Accepted, "IPC handshake");
            control = await PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, token);
            await MuteAsync(control, token);

            Guid id = Guid.NewGuid();
            Check((await client.PrepareAsync(new PlaybackSource
            {
                Kind = PlaybackSourceKind.Http,
                ResourceId = identity,
                Location = bridge.Location,
                FileExtension = track.Extension.Length > 0 ? "." + track.Extension : "",
                ContentLength = readSession.Entry.Length,
                CanSeek = bridge.CanSeek
            }, id, 0, token)).Accepted, "prepare command");

            var ready = await WaitAsync(client, id, reply => reply.Phase == StreamPhase.Ready, token);
            Check(ready.BufferedMs > 0, "native decoder buffered audio");
            if (ready.CanSeek)
            {
                long destination = Math.Min(10000, ready.DurationMs.GetValueOrDefault() / 2);
                Check(destination > 0, "native decoder reports track duration");
                Check((await client.SeekAsync(id, destination, 1, token)).Accepted, "seek command");
                var sought = await WaitAsync(client, id, reply => reply.SeekId == 1 && reply.Phase == StreamPhase.Ready, token);
                Check(Math.Abs(sought.PositionMs - destination) < 100, "native decoder completed seek");
            }
            else
            {
                Check(!(await client.SeekAsync(id, 1000, 1, token)).Accepted, "sequential source rejects seeking");
            }

            Check((await client.PlayAsync(id, token)).Accepted, "play command");
            var playing = await WaitAsync(client, id, reply => reply.Phase == StreamPhase.Playing, token);
            await WaitAsync(client, id, reply => reply.PositionMs > playing.PositionMs, token);
            Check((await client.PauseAsync(id, token)).Accepted, "pause command");
            await WaitAsync(client, id, reply => reply.Phase == StreamPhase.Paused, token);
            Check((await client.StopAsync(id, token)).Accepted, "stop command");
            Console.WriteLine($"PASS {provider.Kind} {track.Extension} AudioPlayer: authenticated stream, native decode, {(ready.CanSeek ? "seek" : "sequential")}, rendered progress, pause and stop.");
        }
        finally
        {
            ownerStop.Set();
            owner.Join();
            try
            {
                if (player is not null)
                {
                    using (player)
                    {
                        try { await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { player.Kill(entireProcessTree: true); await player.WaitForExitAsync(); }
                        string? stderr = errors is null ? null : await errors;
                        if (output is not null) await output;
                        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
                    }
                }
            }
            finally { if (control is not null) await control.DisposeAsync(); }
        }
    }

    private static void OwnClientMutex(TaskCompletionSource ready, ManualResetEventSlim stop)
    {
        try
        {
            using var mutex = new Mutex(true, IpcConstants.ClientAliveMutexName, out bool created);
            if (!created) throw new IOException("The test IPC scope is already in use.");
            ready.SetResult();
            stop.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception ex) { ready.TrySetException(ex); }
    }

    private static async Task MuteAsync(PipeCommandClient control, CancellationToken token)
    {
        byte[] volume = new byte[BinarySerializer.ChangeVolumeRequestSize];
        BinarySerializer.WriteChangeVolumeRequest(volume, new ChangeVolumeRequest { Volume = 0 });
        var (type, _) = await control.RequestAsync(CommandId.ChangeVolume, volume, Array.Empty<byte>(), token: token);
        Check(type == MessageTypeId.Success, "muted private audio output");
    }

    private static async Task<StreamReply> WaitAsync(StreamingClient client, Guid id, Func<StreamReply, bool> ready, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            var status = await client.StatusAsync(id, timeout.Token);
            if (status.Phase == StreamPhase.Failed || !status.Accepted)
                throw new CheckException($"AudioPlayer stream failed: {status.Error}.");
            if (ready(status)) return status;
            await Task.Delay(50, timeout.Token);
        }
    }

    private static void Check(bool condition, string operation)
    {
        if (!condition) throw new CheckException("AudioPlayer could not complete " + operation + ".");
    }
}
