using System.Buffers.Binary;
using System.Diagnostics;
using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using static WinUIMusicPlayer.Utils.ToolUtils;

internal static class NativeRapidNextProbe
{
    public static async Task RunAsync(string playerPath)
    {
        Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", "rapid-" + Guid.NewGuid().ToString("N"));
        using var alive = new Mutex(true, IpcConstants.ClientAliveMutexName);
        using var server = Process.Start(new ProcessStartInfo(playerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(playerPath)!, UseShellExecute = false, CreateNoWindow = true
        })!;
        string work = Path.Combine(Path.GetTempPath(), "rapid-next-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var transport = await PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, timeout.Token);
            using var statePipe = await PipeStateClient.ConnectAsync(IpcConstants.StatePipeName, timeout.Token);
            Require(transport.InstanceId == statePipe.InstanceId, "control/state pipe player mismatch");
            statePipe.Start();
            byte[] settings = new byte[1024];
            int size = BinarySerializer.WriteIpcSetting(settings, new IpcSetting
            {
                OutputMode = "WasapiShared", BassOutputDeviceId = -1, Latency = 100, Volume = 0.01f,
                DsdGain = 6, DsdPcmFreq = 88200
            });
            await Send(CommandId.UpdateSettings, settings[..size]);
            var songs = Enumerable.Range(0, 5).Select(i => MakeSong(work, i)).ToArray();
            await Burst(TrackTransitionStyle.Crossfade, 0, false);
            await Burst(TrackTransitionStyle.Smart, 40, false);
            await Burst(TrackTransitionStyle.Crossfade, 25, true);
            await Burst(TrackTransitionStyle.Smart, 20, true, clicks: 23);

            var shortSong = MakeSong(work, songs.Length, 1);
            long previousEpoch = 0;
            foreach (long id in new long[] { 1001, 1002 })
            {
                var completion = new TaskCompletionSource<PlaybackEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnEnd(MessageTypeId type, ReadOnlyMemory<byte> bytes)
                {
                    if (type == MessageTypeId.PlayEnded && PlaybackEnded.TryRead(bytes.Span, out var ended)) completion.TrySetResult(ended);
                }
                statePipe.NotificationReceived += OnEnd;
                try
                {
                    byte[] payload = new byte[BinarySerializer.PlayOccurrenceSize];
                    int length = BinarySerializer.WritePlayOccurrence(payload, id, shortSong.Path);
                    await Send(CommandId.PlayOccurrence, payload[..length]);
                    var ended = await completion.Task.WaitAsync(TimeSpan.FromSeconds(6));
                    Require(ended.PlaybackId == id && ended.Epoch > previousEpoch, "Native EOF identified the wrong playback occurrence");
                    previousEpoch = ended.Epoch;
                }
                finally { statePipe.NotificationReceived -= OnEnd; }
            }
            Console.WriteLine("Native EOF identified both successive occurrences of the same WAV file.");

            async Task<byte[]> Send(CommandId command, byte[] payload)
            {
                var response = new byte[IpcConstants.MaxResponseSize];
                var (type, length) = await transport.RequestAsync(command, payload, response).ConfigureAwait(false);
                Require(type == MessageTypeId.Success, $"{command} returned {type}");
                return response[..length];
            }

            async Task Burst(TrackTransitionStyle style, int interval, bool waitForOverlap, int clicks = 4)
            {
                using var host = new Scenario();
                using var services = new ServiceCollection().AddSingleton(host.Player).AddSingleton(host.Coordinator)
                    .AddSingleton(host.Remote).BuildServiceProvider();
                host.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
                host.Lifecycle.TransitionTo(AppPhase.Initializing);
                host.Lifecycle.TransitionTo(AppPhase.Ready);
                using var commands = new PlaybackCommands(host.Lifecycle, host.State, services);
                host.State.CurrentPlayMode = clicks < songs.Length ? PlayMode.RepeatOff : PlayMode.ListLoop;
                host.State.CurrentPlayingList = new(songs);
                AppSettings.Dsp = AppSettings.Dsp with { GaplessPlayback = true, TransitionStyle = style, CrossfadeMilliseconds = 2000 };
                byte[] dsp = new byte[DspProtocol.SettingsSize];
                DspProtocol.WriteSettings(dsp, AppSettings.Dsp);
                await Send(CommandId.UpdateDsp, dsp);

                host.Player.OnPlay = music =>
                {
                    byte[] payload = new byte[BinarySerializer.PlayOccurrenceSize];
                    int length = BinarySerializer.WritePlayOccurrence(payload, host.Player.LastPlaybackId, music.Path);
                    Send(CommandId.PlayOccurrence, payload[..length]).GetAwaiter().GetResult();
                };
                host.Ipc.QueueReply = async () => { await Send(CommandId.QueueNext, host.Ipc.Queued[^1].Write()); return true; };
                host.Ipc.CancelReply = async () => BinaryPrimitives.ReadInt64LittleEndian(
                    await Send(CommandId.QueueNext, new GaplessRequest(0, 0, "").Write()));
                var received = new HashSet<long>();
                host.Ipc.ManualReply = async request =>
                {
                    byte[] payload;
                    if (received.Add(request.Token)) payload = request.Write();
                    else
                    {
                        payload = new byte[TransitionNowQuery.Size];
                        TransitionNowQuery.Write(payload, request.Epoch, request.Token);
                    }
                    return (TransitionNowStatus)(await Send(CommandId.TransitionNow, payload))[0];
                };
                var dispatcher = App.MainWindow.DispatcherQueue;
                void Poll()
                {
                    if (statePipe.TryGetProgress(out var snapshot)) host.Ipc.Progress = snapshot;
                }
                void Notify(MessageTypeId type, ReadOnlyMemory<byte> payload)
                {
                    if (type != MessageTypeId.GaplessTransition) return;
                    var span = payload.Span;
                    long token = BinaryPrimitives.ReadInt64LittleEndian(span);
                    long epoch = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
                    long position = span.Length >= 24 ? BinaryPrimitives.ReadInt64LittleEndian(span[16..]) : 0;
                    dispatcher.TryEnqueue(() => { Poll(); host.Ipc.Transition(token, epoch, position); });
                }
                var timer = dispatcher.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(15);
                timer.Tick += (_, _) => Poll();
                statePipe.NotificationReceived += Notify;
                timer.Start();
                try
                {
                    Poll();
                    await host.Coordinator.PlayAtAsync(0);
                    host.State.IsPlaying = true;
                    await Wait(() => host.Ipc.Progress is { Playing: true, TotalMs: 16000, CurrentMs: > 150 });
                    var clock = Stopwatch.StartNew();
                    for (int i = 1; i <= clicks; i++)
                    {
                        commands.NextCommand.Execute(null);
                        int row = i % songs.Length;
                        Require(host.State.DisplayedPlaybackMusic == songs[row] && host.State.GetSelectedPlaybackIndex() == row,
                            $"click {i}: displayed {host.State.DisplayedPlaybackMusic?.Id}, selected {host.State.GetSelectedPlaybackIndex()}");
                        if (waitForOverlap && i == 1)
                            await Wait(() => host.Ipc.Progress is { TransitionToken: > 0 } && host.State.State.Playback.TransitionPresentation is { Epoch: > 0 });
                        if (interval > 0 && i < clicks) await Task.Delay(interval);
                    }
                    long burstMs = clock.ElapsedMilliseconds;
                    int lastRow = clicks % songs.Length;
                    var last = songs[lastRow];
                    bool AtLastTrack() => host.State.CurrentPlayingMusic == last
                        && host.Ipc.Progress is { Playing: true, CurrentMs: > 200 } p && p.TotalMs == (long)last.Duration.TotalMilliseconds;
                    await Wait(AtLastTrack);
                    await Task.Delay(350);
                    Require(AtLastTrack() && host.State.DisplayedPlaybackMusic == last && host.State.GetSelectedPlaybackIndex() == lastRow,
                        "stale handoff after final commit");
                    Console.WriteLine($"{style}, {clicks} Next commands in {burstMs}ms, duringOverlap={waitForOverlap}: every selection immediate; player confirmed {host.Ipc.Progress?.TotalMs}ms at row {lastRow}");
                }
                finally { timer.Stop(); statePipe.NotificationReceived -= Notify; }
            }
        }
        finally
        {
            if (!server.WaitForExit(5000)) { server.Kill(); server.WaitForExit(); }
            foreach (var file in Directory.EnumerateFiles(work)) File.Delete(file);
            Directory.Delete(work);
        }
    }

    private static Music MakeSong(string directory, int index, int? durationSeconds = null)
    {
        string path = Path.Combine(directory, $"track-{index}.wav");
        int seconds = durationSeconds ?? (16 + index);
        const int rate = 44100;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataBytes = seconds * rate * sizeof(short);
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * sizeof(short));
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(dataBytes);
        for (int i = 0; i < rate * seconds; i++)
            writer.Write((short)(1000 * Math.Sin(i * (220 + index * 100) * 2 * Math.PI / rate)));
        return new Music(100 + index) { Path = path, Duration = TimeSpan.FromSeconds(seconds), Album = $"Album {index}" };
    }

    private static async Task Wait(Func<bool> ready)
    {
        var deadline = Environment.TickCount64 + 7000;
        while (!ready())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Native rapid Next condition timed out.");
            await Task.Delay(15);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
