using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", "dav-recovery-" + Guid.NewGuid().ToString("N"));
using var ui = new UiContext();
SynchronizationContext.SetSynchronizationContext(ui);
App.MainWindow.DispatcherQueue.Attach();
Task run = RunAsync();
ui.Run(run);
await run;

static async Task RunAsync()
{
    await using var peer = new PlaybackPeer();
    var origin = new Fixture();
    using var transport = new WebDavTransport();
    string directory = Path.Combine(Path.GetTempPath(), "dav-recovery-" + Guid.NewGuid().ToString("N"));
    var cache = new RemoteAudioCache();
    cache.Configure(false, directory, 32L * 1024 * 1024);
    var state = new AppViewModel();
    var network = new Music(1, 10);
    var network2 = new Music(2, 10);
    var local = new Music(3);
    state.CurrentPlayingList = [network, network2, local];
    state.State.Queue.FindIndex = song => state.CurrentPlayingList.IndexOf(song);
    await using var library = new WebDavLibraryService(new(), transport, cache, NullLogger<WebDavLibraryService>.Instance)
        { Root = origin.Root };
    library.Attach(state);
    var statistics = new PlaybackStatsService();
    var remote = new RemotePlaybackService(library, transport, cache, state, statistics, NullLogger<RemotePlaybackService>.Instance);
    var player = new BassPlayerCommandService();
    var lifecycle = new AppLifecycle();
    using var coordinator = new PlaybackCoordinator(state, player, statistics, new(lifecycle),
        new(lifecycle, NullLogger<ShutdownCoordinator>.Instance), NullLogger<PlaybackCoordinator>.Instance, remote, library);
    int sourceNotifications = 0;
    network.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName != nameof(Music.IsRemoteOffline)) return;
        Check(App.MainWindow.DispatcherQueue.HasThreadAccess, "song availability notification stays on UI thread");
        sourceNotifications++;
    };
    int failures = 0;
    remote.Failed += (_, _, _) => failures++;
    await coordinator.PlayAtAsync(0);
    await WaitAsync(() => state.IsPlaying);
    Check((await peer.ReadAsync(0, 1023)).AsSpan().SequenceEqual(origin.Bytes.AsSpan(0, 1024)), "real streaming session supplies origin bytes");
    await origin.DisposeAsync();
    try { await peer.ReadAsync(2 * 1024 * 1024, 2 * 1024 * 1024 + 1023); }
    catch (HttpRequestException) { }
    await WaitAsync(() => state.CurrentPlayingMusic == local);
    Check(library.IsSourceOffline(10) && network.IsRemoteOffline && network2.IsRemoteOffline && sourceNotifications == 1,
        "origin disconnect publishes source and song state through production library");
    Check(player.Played.SequenceEqual([local]) && peer.Prepares == 1 && failures == 1,
        "source read failure advances once to local while decoder still reports Playing");
    int connectsAfterFailure = library.Connects;
    await coordinator.PlayAtAsync(0);
    Check(library.Connects == connectsAfterFailure && state.CurrentPlayingMusic == local,
        "next click within failure deadline does not reconnect the offline source");

    // Seed a complete exact-version cache, with all credential access deliberately rejected.
    cache.Configure(true, directory, 32L * 1024 * 1024);
    await using (var writer = cache.Acquire(network.Path, library.Entry)!)
    {
        for (int offset = 0; offset < origin.Bytes.Length; offset += 65536)
        {
            int count = Math.Min(65536, origin.Bytes.Length - offset);
            Check(writer.TryWrite(offset, origin.Bytes.AsSpan(offset, count)), "cache fixture write");
            while (!writer.Contains(offset, count)) await Task.Delay(1);
        }
    }
    library.RejectCredentials = true;
    await coordinator.PlayAtAsync(0);
    await WaitAsync(() => state.IsPlaying);
    Check(network.IsRemoteCached && network.IsPlayable && library.Connects == connectsAfterFailure,
        "offline cache selection bypasses real availability probe and credential lookup");
    Check((await peer.ReadAsync(0, origin.Bytes.Length - 1)).AsSpan().SequenceEqual(origin.Bytes),
        "real IPC session plays complete cached bytes with origin shut down");
    cache.Clear();
    Check((await peer.ReadAsync(0, 1023)).Length == 1024, "clearing cache preserves current playback lease");
    await coordinator.PlayAsync(local);
    Check(!cache.HasCompleteFile(network.Path, library.Entry), "old cache lease is released after switching local");
    await coordinator.PlayAtAsync(0);
    Check(!network.IsRemoteCached && state.CurrentPlayingMusic == local && library.Connects == connectsAfterFailure,
        "stale cache icon is rechecked and cleared without probing during cooldown");

    // A decoder failure is recoverable but does not prove a source outage.
    await using var secondOrigin = new Fixture();
    library.Root = secondOrigin.Root;
    library.RejectCredentials = false;
    await coordinator.PlayAsync(network);
    await WaitAsync(() => state.IsPlaying);
    Check(!library.IsSourceOffline(10), "explicit retry bypasses cooldown and restores source status");
    library.MissingTrack = 99;
    await coordinator.PlayAsync(new Music(99, 10));
    Check(state.CurrentPlayingMusic == network, "failed pending selection preserves current streaming session");
    peer.FailDecoder();
    await WaitAsync(() => state.CurrentPlayingMusic == network2 && peer.Prepares == 4 && state.IsPlaying && state.State.Playback.PendingSelection is null);
    peer.FailDecoder();
    await WaitAsync(() => state.CurrentPlayingMusic == local);
    Check(true, "failed pending selection leaves current session monitoring and recovery alive");
    Check(!library.IsSourceOffline(10) && failures == 3, "decoder failures skip tracks without marking healthy source offline");
    await coordinator.PlayAsync(network);
    await WaitAsync(() => state.IsPlaying);
    await remote.SetIntentAsync(false);
    peer.FailDecoder();
    await WaitAsync(() => failures == 4);
    Check(state.CurrentPlayingMusic == network && !remote.WantsPlay, "failure while paused does not auto-advance or resume playback");
    await remote.StopAsync();
    cache.Clear();
    Console.WriteLine("PASS: remote recovery integration completed");
}

static async Task WaitAsync(Func<bool> condition)
{
    long deadline = Environment.TickCount64 + 5000;
    while (!condition())
    {
        if (Environment.TickCount64 > deadline) throw new TimeoutException("remote regression condition");
        await Task.Delay(5);
    }
}
static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    if (message != "cache fixture write") Console.WriteLine("PASS: " + message);
}

sealed class UiContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
    public void Run(Task task)
    {
        long deadline = Environment.TickCount64 + 45000;
        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("remote regression stalled");
            if (_queue.TryTake(out var item, 50)) item.Callback(item.State);
        }
    }
    public void Dispose() => _queue.Dispose();
}
