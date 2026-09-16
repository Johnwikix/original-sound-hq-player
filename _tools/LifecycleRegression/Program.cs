using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
foreach (bool ready in new[] { false, true })
{
    var lifecycle = new AppLifecycle();
    lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
    if (ready) { lifecycle.TransitionTo(AppPhase.Initializing); lifecycle.TransitionTo(AppPhase.Ready); }
    var order = new List<string>();
    var shutdown = new ShutdownCoordinator(lifecycle, NullLogger<ShutdownCoordinator>.Instance);
    shutdown.RegisterCleanup(() => order.Add("window"));
    shutdown.RegisterCleanup(() => { order.Add("audio"); throw new IOException("cleanup failure"); });
    shutdown.RegisterSave(() => { order.Add("save"); return Task.CompletedTask; });
    var hold = new TaskCompletionSource();
    if (ready) shutdown.HostStarted(async () => { order.Add("host"); await hold.Task; });
    Task<bool> exiting = shutdown.ShutdownAsync();
    Check(!await shutdown.ShutdownAsync(), "Repeated shutdown must not repeat work");
    hold.SetResult();
    Check(await exiting, "First caller owns shutdown");
    Check(lifecycle.StoppingToken.IsCancellationRequested, "Exit must cancel in-flight startup waits");
    Check(string.Join(',', order) == (ready ? "save,host,audio,window" : "audio,window"), "Shutdown ordering or early persistence is wrong");
    try { lifecycle.TransitionTo(AppPhase.Ready); throw new Exception("Shutdown resurrected"); }
    catch (OperationCanceledException) { }
    bool lateDisposed = false;
    shutdown.RegisterCleanup(() => lateDisposed = true);
    Check(lateDisposed, "Late-created resource leaked after shutdown drained");
}
Console.WriteLine("PASS: early exit skips persistence/host; ready exit saves once; failure does not skip cleanup; stopping cannot become ready.");

var life = new AppLifecycle();
var state = new AppViewModel { CurrentPlayingMusic = new(1), CurrentPlayingList = [new(1), new(2)] };
int created = 0;
using var services = new ServiceCollection().AddSingleton(state)
    .AddSingleton(sp => { created++; return new BassPlayerCommandService(state); })
    .AddSingleton<WinUIMusicPlayer.View.MainPage>().AddSingleton<MusicBrowseViewModel>().BuildServiceProvider();
WinUIMusicPlayer.App.Services = services;
using var commands = new PlaybackCommands(life, state, services);
using var tray = new TrayViewModel(life, state, new WinUIMusicPlayer.DesktopLyrics.DesktopLyricsViewModel(), commands);
Check(!tray.OpenSettingsCommand.CanExecute(null) && !tray.ToggleDesktopLyricsCommand.CanExecute(null), "Tray bypassed startup availability");
tray.OpenSettingsCommand.Execute(null); tray.ToggleDesktopLyricsCommand.Execute(null);
Check(!tray.DesktopLyrics.IsEnabled, "Disabled tray action mutated state");
tray.ShowWindowCommand.Execute(null); await tray.ExitCommand.ExecuteAsync(null);
Check(WinUIMusicPlayer.App.MainWindow.Visible && WinUIMusicPlayer.App.Exits == 1, "Show/exit must be usable before readiness");
Check(created == 0, "Constructing command surface must not resolve playback backend");
await commands.ToggleCommand.ExecuteAsync(null); commands.NextCommand.Execute(null); commands.SeekCommand.Execute(50L);
Check(created == 0 && !commands.ToggleCommand.CanExecute(null), "Commands bypassed startup guard");
life.TransitionTo(AppPhase.Initializing); life.TransitionTo(AppPhase.Ready);
Check(tray.IsReady && tray.OpenSettingsCommand.CanExecute(null), "Ready transition did not enable tray");
tray.OpenSettingsCommand.Execute(null);
Check(services.GetRequiredService<WinUIMusicPlayer.View.MainPage>().SettingsOpened == 1, "Tray settings executed before ready or failed afterwards");
await commands.PlayCommand.ExecuteAsync(null);
var player = services.GetRequiredService<BassPlayerCommandService>();
await commands.PlayCommand.ExecuteAsync(null);
Check(player.Toggles == 1, "Explicit Play toggled an already playing track");
await commands.PauseCommand.ExecuteAsync(null); await commands.PauseCommand.ExecuteAsync(null);
Check(player.Toggles == 2, "Explicit Pause toggled a paused track");
player.Pending = new TaskCompletionSource();
var playing = commands.PlayCommand.ExecuteAsync(null);
await commands.ToggleCommand.ExecuteAsync(null);
Check(player.Toggles == 3, "Multiple input surfaces sent concurrent toggles");
player.Pending.SetResult(); await playing; player.Pending = null;
await commands.PreviousCommand.ExecuteAsync(null);
Check(services.GetRequiredService<MusicBrowseViewModel>().LastPlayed?.Id == 2, "Previous track wrap changed");
commands.SeekCommand.Execute(-10L); Check(player.Position == 0, "Seek guard/clamp failed");
int availabilityChanges = 0;
commands.NextCommand.CanExecuteChanged += (_, _) => availabilityChanges++;
state.CurrentPlayingList.Clear();
Check(!commands.NextCommand.CanExecute(null) && availabilityChanges > 0, "Collection edits did not update command availability");
state.CurrentPlayingList.Add(new(1));
life.TryBeginExit(out _); commands.NextCommand.Execute(null); await commands.ToggleCommand.ExecuteAsync(null);
Check(!tray.IsReady && !tray.OpenSettingsCommand.CanExecute(null), "Tray did not disable during exit");
Check(player.NextCalls == 0 && player.Toggles == 3, "Commands reached backend after shutdown");
Console.WriteLine("PASS: lazy backend, shared startup/shutdown gate, explicit Play/Pause, concurrent inputs and previous-track wrap.");

string directory = Path.Combine(Path.GetTempPath(), "music-watcher-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var database = new MusicDatabaseService { Folders = [new(directory)] };
var watcher = new LibraryWatcherService(database, state, NullLogger<LibraryWatcherService>.Instance);
try
{
    Check(database.Reads == 0, "Watcher constructor performed IO");
    await watcher.StartAsync(); await watcher.StartAsync();
    Check(database.Reads == 1, "Repeated startup created duplicate watchers");
    await File.WriteAllTextAsync(Path.Combine(directory, "track.txt"), "changed");
    for (int i = 0; i < 60 && AutoRescanService.Scans == 0; i++) await Task.Delay(50);
    Check(AutoRescanService.Scans > 0, "Real filesystem change did not reach scan consumer");
    await watcher.StopAsync(); await watcher.StopAsync();
    int scans = AutoRescanService.Scans;
    await File.WriteAllTextAsync(Path.Combine(directory, "track.txt"), "after shutdown");
    await Task.Delay(1200);
    Check(AutoRescanService.Scans == scans, "Watcher continued scanning after stop");
}
finally { await watcher.StopAsync(); Directory.Delete(directory, true); }
Console.WriteLine("PASS: no constructor IO, idempotent watcher start/stop, real file events, no scanning after stop.");
