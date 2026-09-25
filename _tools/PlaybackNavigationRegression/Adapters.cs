using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer
{
#if REAL_WINUI
    public static class App { public static Microsoft.UI.Xaml.Window MainWindow { get; set; } = null!; public static IServiceProvider Services = null!; }
#else
    public static class App { public static Window MainWindow { get; } = new(); public static IServiceProvider Services = null!; }
    public sealed class Window { public UiQueue DispatcherQueue { get; } = new(); }
    public sealed class UiQueue
    {
        private SynchronizationContext _context = null!;
        private int _thread;
        public void Attach() { _context = SynchronizationContext.Current!; _thread = Environment.CurrentManagedThreadId; }
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _thread;
        public void Post(Action action) => _context.Post(_ => action(), null);
        public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueueHandler action) { Post(() => action()); return true; }
    }
#endif
}
#if !REAL_WINUI
namespace Microsoft.UI.Dispatching { public delegate void DispatcherQueueHandler(); }
#endif
namespace CommunityToolkit.WinUI
{
    public static class Dispatch
    {
#if REAL_WINUI
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
#else
        public static Task EnqueueAsync(this WinUIMusicPlayer.UiQueue queue, Action action)
#endif
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.TryEnqueue(() => { try { action(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } });
            return done.Task;
        }
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed class Music(int id, int source = 0) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string Path => $"webdav://{source}/dav/tone.flac";
        public TimeSpan Duration { get; set; }
        public int Id => id;
        public int SourceId => source;
        public bool IsRemote => source != 0;
        public bool IsRemoteOffline
        {
            get;
            set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(IsPlayable)); }
        }
        public bool IsRemoteCached
        {
            get;
            set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(IsPlayable)); }
        }
        public bool IsPlayable => IsRemoteCached || !IsRemoteOffline;
    }
    public sealed record WebDavSource(int Id) { public bool Enabled => true; }
}
namespace WinUIMusicPlayer.Utils { public static class ToolUtils { public static string GetString(string key) => key; } }
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public bool CanPublishState => CanStartPlayback;
        public bool IsPlaybackEngineReady => true;
        public bool CanStartPlayback { get; set; } = true;
        public List<Music> CurrentPlayingList { get; set; } = [];
        public Music? CurrentPlayingMusic { get; set => SetProperty(ref field, value); }
        public Music? SelectedPlaybackMusic => State.Playback.PendingSelection?.Music ?? CurrentPlayingMusic;
        public int GetSelectedPlaybackIndex() => State.Playback.PendingSelection is { } pending ? (int)pending.EntryId - 1 : GetCurrentIndex();
        public int GetCurrentIndex() => CurrentPlayingList.IndexOf(CurrentPlayingMusic!);
        public bool IsPlaying { get; set => SetProperty(ref field, value); }
        public string RemotePlaybackStatus { get; set; } = "";
        public List<string> UILyrics { get; set; } = [];
        public AppState State { get; } = new();
        public List<Music> SongsSource => CurrentPlayingList;
        public void StartProgressTimer() { }
        public void BeginProgressSeek(long ms, long id) { }
        public void CancelProgressSeek(long id) { }
        public void StopProgressTimer() { }
        public void LoadLyricsToUI(Music music) { }
        public void UpdateProgressTimerUI() { }
    }
    public sealed class ShellState { public bool InfoBarIsOpen { get; set; } public string InfoBarTitle { get; set; } = ""; public string InfoBarMessage { get; set; } = ""; }
    public sealed class AppState { public ShellState Shell { get; } = new(); public WinUIMusicPlayer.State.PlaybackState Playback { get; } = new(); public QueueState Queue { get; } = new(); }
    public sealed class QueueState
    {
        public Func<Music, int> FindIndex { get; set; } = _ => -1;
        public int IndexOf(Music music) => FindIndex(music);
        public long CurrentEntryId { get; set; }
        public long EntryIdAt(int index) => index < 0 ? 0 : index + 1;
        public void SelectEntry(long id, Music music) => CurrentEntryId = id != 0 ? id : EntryIdAt(IndexOf(music));
    }
}
#if !REAL_REMOTE
namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed class WebDavException(string code) : IOException { public string Code => code; }
}
#endif
namespace WinUIMusicPlayer.Services
{
    public sealed class BassPlayerCommandService
    {
        public List<Music> Played { get; } = [];
        public int Ends;
        public void PlayMusic(Music music)
        {
            if (!App.MainWindow.DispatcherQueue.HasThreadAccess) throw new Exception("play called outside UI thread");
            Played.Add(music);
        }
        public void MusicEnd() => Ends++;
        public void PlayNextTrack() { }
        public Task PlayButton() => Task.CompletedTask;
        public void ChangeWaveChannelTime(long ms) { }
    }
    public sealed class PlaybackStatsService { public void FlushSession() { } public void StartSession(Music music) { } }
    #if !REAL_REMOTE
    public sealed class RemotePlaybackService
    {
        public int StopDelayMs;
        public int OpenDelayMs;
        public bool NeedsStart => true;
        public bool WantsPlay => false;
        public Task SetIntentAsync(bool? playing) => Task.CompletedTask;
        public List<Music> Played { get; } = [];
        public event Action<Music, long, bool>? Failed;
        private long _generation;
        public long BeginSelection() => ++_generation;
        public void Fail(Music music, bool advance = true) => Failed?.Invoke(music, _generation, advance);
        public Task StopAsync() => Task.Run(() => Thread.Sleep(StopDelayMs));
        public Task PlayAsync(Music music, long generation, CancellationToken token)
        {
            Thread.Sleep(OpenDelayMs);
            token.ThrowIfCancellationRequested();
            Played.Add(music);
            return Task.CompletedTask;
        }
    }
    public sealed class WebDavLibraryService
    {
        public HashSet<int> Offline { get; } = [];
        public WinUIMusicPlayer.Services.WebDav.WebDavAvailability Availability { get; } = new();
        public List<int> Probes { get; } = [];
        public Func<WebDavSource, CancellationToken, Task<bool>> Probe { get; set; } = (_, _) => Task.FromResult(true);
        public bool IsSourceOffline(int source) => Offline.Contains(source);
        public async Task<(WebDavSource, object)> ResolveAsync(Music music) { await Task.Yield(); return (new(music.SourceId), new()); }
        public async Task<bool> CanPlayAsync(Music music, bool retryImmediately, CancellationToken token)
        {
            if (music.IsRemoteCached) return true;
            if (!retryImmediately && Availability.ShouldDefer(music.SourceId)) return false;
            return await EnsureAvailableAsync(new(music.SourceId), token);
        }
        public async Task<bool> EnsureAvailableAsync(WebDavSource source, CancellationToken token)
        {
            Probes.Add(source.Id);
            bool available = await Probe(source, token);
            if (!available) { Offline.Add(source.Id); Availability.ReportFailure(source.Id); }
            else Offline.Remove(source.Id);
            // Deliberately leave row flags unchanged: UI availability publication can arrive later.
            return available;
        }
    }
    #endif
}
