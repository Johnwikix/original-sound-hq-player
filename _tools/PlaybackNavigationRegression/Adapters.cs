using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer
{
#if REAL_WINUI
    public static class App { public static Microsoft.UI.Xaml.Window MainWindow { get; set; } = null!; public static IServiceProvider Services = null!; }
#else
    public static class App { public static Window MainWindow { get; } = new(); public static IServiceProvider Services = null!; }
    public sealed class Window
    {
        public UiQueue DispatcherQueue { get; } = new();
        public ContentStub Content { get; } = new();
    }
    public sealed class ContentStub { public object XamlRoot { get; } = new(); }
    public sealed class UiQueue
    {
        private SynchronizationContext _context = null!;
        private int _thread;
        public void Attach() { _context = SynchronizationContext.Current!; _thread = Environment.CurrentManagedThreadId; }
        public Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTimer() => new(this);
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _thread;
        public void Post(Action action) => _context.Post(_ => action(), null);
        public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueueHandler action) { Post(() => action()); return true; }
    }
#endif
}
#if !REAL_WINUI
namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();
    public sealed class DispatcherQueueTimer(WinUIMusicPlayer.UiQueue queue)
    {
        private System.Threading.Timer? _timer;
        public TimeSpan Interval { get; set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() => _timer ??= new System.Threading.Timer(_ => queue.Post(() => { if (_timer != null) Tick?.Invoke(this, new()); }), null, Interval, Interval);
        public void Stop() { _timer?.Dispose(); _timer = null; }
    }
}
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
        public int SongId { get; set; }
        public int? SongPreferredMusicId { get; set; }
        public bool IsDetached { get; set; }
        public bool IsMissing { get; set; }
        public string Title { get; set; } = "Song";
        public string Author { get; set => SetProperty(ref field, value); } = "Artist";
        public string Album { get; set => SetProperty(ref field, value); } = "Album";
        public string AlbumArtist { get; set => SetProperty(ref field, value); } = "";
        public int DiskNumber { get; set => SetProperty(ref field, value); }
        public int TrackNumber { get; set => SetProperty(ref field, value); }
        public string CodecId { get; set; } = "";
        public string Extension { get; set; } = "";
        public int BitDepth { get; set; }
        public bool? TitleFromFileName { get; set; }
        public int EncodingKind { get; set; }
        public int SampleRate { get; set; }
        public int BitRate { get; set; }
        public int Channel { get; set; }
        public string Path { get; set => SetProperty(ref field, value); } = source == 0 ? System.IO.Path.GetFullPath($"track-{id}.wav") : $"webdav://{source}/dav/tone.flac";
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
        public bool IsPlayable => !IsDetached && (IsRemoteCached || !IsMissing && !IsRemoteOffline);
    }
    public sealed record WebDavSource(int Id)
    {
        public bool Enabled => true;
        public string Name => "Test source";
        public string BaseUri => "http://127.0.0.1/dav/";
    }
    public sealed class WebDavCacheSettings { public bool Enabled { get; set; } public int LimitGiB { get; set; } = 10; }
    #if !REAL_WINUI
    public sealed class BulkObservableCollection<T> : System.Collections.ObjectModel.ObservableCollection<T>
    {
        public BulkObservableCollection() { }
        public BulkObservableCollection(IEnumerable<T> items) : base(items) { }
        public void AddRange(IEnumerable<T> items) => InsertRange(Count, items);
        public void InsertRange(int index, IEnumerable<T> items)
        {
            foreach (var item in items) Items.Insert(index++, item);
            OnPropertyChanged(new(nameof(Count)));
            OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }
    #endif
    public static class AppSettings
    {
        public static string MusicCoverCache { get; set; } = Path.GetTempPath();
        public static event EventHandler? PlaybackTransitionsChanged;
        public static BassPlayerIpc.Shared.DspSettings Dsp
        {
            get => field;
            set
            {
                bool changed = field.GaplessPlayback != value.GaplessPlayback || field.TransitionStyle != value.TransitionStyle
                    || field.CrossfadeMilliseconds != value.CrossfadeMilliseconds;
                field = value;
                if (changed) PlaybackTransitionsChanged?.Invoke(null, EventArgs.Empty);
            }
        } = new();
    }
}
namespace WinUIMusicPlayer.Utils { public static class ToolUtils { public enum PlayMode { SingleLoop, ListLoop, RandomLoop, RepeatOff } public static string GetString(string key) => key; } }
namespace WinUIMusicPlayer.Extensions { }
namespace WinUIMusicPlayer.Helper
{
    public static class DialogHelper { public static Task<bool> ShowConfirmAsync(object root, string titleKey) => Task.FromResult(true); }
}
namespace WinUIMusicPlayer.Services.WebDav
{
#if !REAL_REMOTE
    public static class WebDavCachePaths { public static string Root(string path) => Path.Combine(path, "WebDav"); }
    public sealed class RemoteAudioCache { public long GetSize() => 0; public void Clear() { } }
#endif
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public AppViewModel()
        {
            State.Queue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(State.Queue.Playing)) OnPropertyChanged(nameof(CurrentPlayingList));
                if (e.PropertyName == nameof(State.Queue.Mode)) OnPropertyChanged(nameof(CurrentPlayMode));
            };
        }
        public bool CanPublishState => CanStartPlayback;
        public bool IsPlaybackEngineReady => true;
        public bool CanStartPlayback { get; set; } = true;
        public BulkObservableCollection<Music> CurrentPlayingList { get => State.Queue.Playing; set => State.Queue.Replace(value); }
        public WinUIMusicPlayer.Utils.ToolUtils.PlayMode CurrentPlayMode { get => State.Queue.Mode; set => State.Queue.Mode = value; }
        public Music? CurrentPlayingMusic
        {
            get => State.Playback.CurrentPlayingMusic;
            set
            {
                if (ReferenceEquals(State.Playback.CurrentPlayingMusic, value)) return;
                State.Playback.CurrentPlayingMusic = value;
                OnPropertyChanged();
            }
        }
        public Music? DisplayedPlaybackMusic => State.Playback.DisplayedPlaybackMusic;
        public bool CanSeekDisplayedPlayback => State.Playback.TransitionPresentation is null;
        public Music? SelectedPlaybackMusic => State.Playback.PendingSelection?.Music ?? CurrentPlayingMusic;
        public int GetSelectedPlaybackIndex()
        {
            long entryId = State.Playback.PendingSelection?.EntryId ?? State.Playback.TransitionPresentation?.EntryId ?? 0;
            if (entryId > 0)
                for (int i = 0; i < CurrentPlayingList.Count; i++)
                    if (State.Queue.EntryIdAt(i) == entryId) return i;
            return GetCurrentIndex();
        }
        public int CurrentIndexLookups;
        public int GetCurrentIndex() { CurrentIndexLookups++; return State.Queue.IndexOf(CurrentPlayingMusic); }
        public bool IsPlaying { get; set => SetProperty(ref field, value); }
        public double ProgressSlider { get; set; }
        public double ProgressSliderMax { get; set; }
        public string RemotePlaybackStatus { get; set; } = "";
        public List<string> UILyrics { get; set; } = [];
        public AppState State { get; } = new();
        public List<Music>? LibrarySongs { get; set; }
        public IEnumerable<Music> SongsSource => LibrarySongs is not null ? LibrarySongs : CurrentPlayingList;
        public Music? FindById(int id) => SongsSource.FirstOrDefault(m => m.Id == id);
        public IReadOnlyList<Music> FindVersions(int songId) => SongsSource.Where(m => m.SongId == songId).ToArray();
        public string MusicCoverCache => WinUIMusicPlayer.Model.AppSettings.MusicCoverCache;
        public void RefreshDataSource() { }
        public void StartProgressTimer() { }
        public void BeginProgressSeek(long ms, long id) { }
        public void CancelProgressSeek(long id) { }
        public void StopProgressTimer() { }
        public List<Music> LyricsLoads { get; } = [];
        public void LoadLyricsToUI(Music music) => LyricsLoads.Add(music);
        public void UpdateProgressTimerUI() { }
    }
    public sealed class ShellState { public bool InfoBarIsOpen { get; set; } public string InfoBarTitle { get; set; } = ""; public string InfoBarMessage { get; set; } = ""; }
    public sealed class AppState
    {
        public ShellState Shell { get; } = new();
        public WinUIMusicPlayer.State.PlaybackState Playback { get; } = new();
        public WinUIMusicPlayer.State.PlaybackQueueState Queue { get; } = new();
        public BrowseState Browse { get; } = new();
        public PreferencesState Preferences { get; } = new();
    }
    public sealed class BrowseState { public int SourceFilterId { get; set; } }
    public sealed class PreferencesState : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string MusicCoverCache { get; set => SetProperty(ref field, value); } = Path.GetTempPath();
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
#if !REAL_REMOTE
    public sealed class MusicDatabaseService
    {
        public Task<List<WebDavSource>> GetWebDavSourcesAsync() => Task.FromResult(new List<WebDavSource> { new(10) });
        public Task<Dictionary<int, int>> GetWebDavSourceTrackCountsAsync() => Task.FromResult(new Dictionary<int, int>());
        public Task<int> GetWebDavSourceTrackCountAsync(int sourceId) => Task.FromResult(0);
        public Task<WebDavCacheSettings> GetWebDavCacheSettingsAsync() => Task.FromResult(new WebDavCacheSettings());
    }
    public sealed class LibraryQueries { public void SetSourceFilter(int sourceId) { } }
    public sealed record WebDavScanStatus(int SourceId, string Phase, int Found, int Tagged, string? Error = null);
#endif
    public sealed class BassPlayerCommandService
    {
        public WinUIMusicPlayer.ViewModel.AppViewModel? State;
        public PlaybackCoordinator? Coordinator;
        public List<Music> Played { get; } = [];
        public int Ends;
        public Action<Music>? OnPlay;
        public long LastPlaybackId { get; private set; }
        public void PlayMusic(Music music, long playbackId)
        {
            if (!App.MainWindow.DispatcherQueue.HasThreadAccess) throw new Exception("play called outside UI thread");
            LastPlaybackId = playbackId;
            Played.Add(music);
            OnPlay?.Invoke(music);
        }
        public void MusicEnd(bool preserveInterruptedProgress = false)
        {
            Ends++;
            if (!preserveInterruptedProgress && State is not null) State.ProgressSlider = 0;
        }
        public void PlayNextTrack() { if (Coordinator is { } coordinator) _ = coordinator.PlayNextAsync(); }
        public Task PlayButton() => Task.CompletedTask;
        public void ChangeWaveChannelTime(long ms) { }
    }
    public sealed class IpcService
    {
        public BassPlayerIpc.Shared.ProgressSnapshot? Progress
        {
            get => field;
            set
            {
                var previous = field;
                field = value;
                if ((previous?.TransitionToken ?? 0) != (value?.TransitionToken ?? 0)
                    || (previous?.TransitionEpoch ?? 0) != (value?.TransitionEpoch ?? 0)) TransitionProgressChanged?.Invoke();
            }
        }
        public List<BassPlayerIpc.Shared.GaplessRequest> Queued { get; } = [];
        public List<BassPlayerIpc.Shared.GaplessRequest> Manual { get; } = [];
        public List<(long Epoch, long Token)> ManualPolls { get; } = [];
        public event Action<BassPlayerIpc.Shared.MessageTypeId, ReadOnlyMemory<byte>>? NotificationReceived;
        public event Action? DspStateChanged;
        public event Action? TransitionProgressChanged;
        public BassPlayerIpc.Shared.DspStateSnapshot? CurrentDspState { get; private set; }
        public Func<Task<bool>>? QueueReply;
        public Func<Task<long>>? CancelReply;
        public Func<BassPlayerIpc.Shared.GaplessRequest, Task<BassPlayerIpc.Shared.TransitionNowStatus>>? ManualReply;
        public void RebuildOutput(long generation)
        {
            CurrentDspState = new(generation, new BassPlayerIpc.Shared.DspState(0, false, 2, 0, 0, 0, true)
                { OutputGeneration = generation });
            DspStateChanged?.Invoke();
        }
        public bool TryGetProgressSnapshot(out BassPlayerIpc.Shared.ProgressSnapshot snapshot) { snapshot = Progress ?? default; return Progress.HasValue; }
        public async Task<bool> QueueNextAsync(BassPlayerIpc.Shared.GaplessRequest request)
        {
            Queued.Add(request);
            if (QueueReply != null) return await QueueReply();
            await Task.Yield();
            return true;
        }
        public async Task<(long Token, long Epoch)> CancelQueuedNextAsync()
        {
            Queued.Add(new(0, 0, ""));
            if (CancelReply != null) return (await CancelReply(), Progress?.Epoch ?? 0);
            long token = Progress?.GaplessToken ?? 0;
            await Task.Yield();
            return (token, Progress?.Epoch ?? 0);
        }
        public Task<BassPlayerIpc.Shared.TransitionNowStatus> TransitionNowAsync(BassPlayerIpc.Shared.GaplessRequest request, CancellationToken token = default)
        {
            Manual.Add(request);
            return ManualStatusAsync(request, token);
        }
        public Task<BassPlayerIpc.Shared.TransitionNowStatus> PollTransitionNowAsync(long epoch, long transitionToken, CancellationToken token)
        {
            ManualPolls.Add((epoch, transitionToken));
            return ManualStatusAsync(Manual.Last(request => request.Epoch == epoch && request.Token == transitionToken), token);
        }
        private Task<BassPlayerIpc.Shared.TransitionNowStatus> ManualStatusAsync(BassPlayerIpc.Shared.GaplessRequest request, CancellationToken token)
            => ManualReply?.Invoke(request).WaitAsync(token) ?? Task.FromResult(BassPlayerIpc.Shared.TransitionNowStatus.Unavailable);
        public void Transition(long token, long epoch, long positionMs = 0)
        {
            byte[] bytes = new byte[positionMs > 0 ? 24 : 16];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, token);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), epoch);
            if (positionMs > 0) System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), positionMs);
            NotificationReceived?.Invoke(BassPlayerIpc.Shared.MessageTypeId.GaplessTransition, bytes);
        }
    }
    public sealed class PlaybackStatsService
    {
        public int Starts;
        public void FlushSession() { }
        public void StartSession(Music music) { }
        public Task RecordPlaybackStartAsync(Music music) { Starts++; return Task.CompletedTask; }
    }
    #if !REAL_REMOTE
    public sealed class RemotePlaybackService
    {
        public int StopDelayMs;
        public int OpenDelayMs;
        public bool NeedsStart { get; set; } = true;
        public bool WantsPlay { get; private set; }
        public Task SetIntentAsync(bool? playing)
        {
            if (playing.HasValue) WantsPlay = playing.Value;
            return Task.CompletedTask;
        }
        public List<Music> Played { get; } = [];
        public event Action<Music, long, bool>? Failed;
        public event Action<Music, BassPlayerIpc.Shared.ProgressSnapshot, bool>? PlaybackStarted;
        public void Confirm(Music music) => PlaybackStarted?.Invoke(music, new(1, _generation, 0, 10000, 0, true), false);
        private long _generation;
        public int PreservedStops { get; private set; }
        public BassPlayerIpc.Shared.ProgressSnapshot? GetProgress() => null;
        public long BeginSelection(Music music, bool resumeInterrupted = false)
        {
            WantsPlay = true;
            return ++_generation;
        }
        public void Fail(Music music, bool advance = true) => Failed?.Invoke(music, _generation, advance);
        public Task StopAsync() => Task.Run(() => Thread.Sleep(StopDelayMs));
        public Task StopAsync(bool preserveFailedProgress)
        {
            if (preserveFailedProgress) { PreservedStops++; _generation++; }
            return Task.CompletedTask;
        }
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
        public event Action<WebDavScanStatus>? StatusChanged;
        public event Action<int, bool>? SourceAvailabilityChanged;
        public event Action? SourcesChanged;
        public HashSet<int> Offline { get; } = [];
        public WinUIMusicPlayer.Services.WebDav.WebDavAvailability Availability { get; } = new();
        public List<int> Probes { get; } = [];
        public Func<WebDavSource, CancellationToken, Task<bool>> Probe { get; set; } = (_, _) => Task.FromResult(true);
        public bool IsSourceOffline(int source) => Offline.Contains(source);
        public WebDavScanStatus? GetStatus(int sourceId) => null;
        public void PublishAvailability(int sourceId, bool offline)
        {
            if (offline) Offline.Add(sourceId); else Offline.Remove(sourceId);
            SourceAvailabilityChanged?.Invoke(sourceId, offline);
        }
        public Task ApplyCacheSettingsAsync(WebDavCacheSettings settings) => Task.CompletedTask;
        public Task ScanAsync(WebDavSource source) => Task.CompletedTask;
        public Task CancelScanAsync(int sourceId) => Task.CompletedTask;
        public Task RemoveSourceAsync(WebDavSource source) => Task.CompletedTask;
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
    public static class WebDavText { public static string Error(string code) => code; }
    #endif
}
