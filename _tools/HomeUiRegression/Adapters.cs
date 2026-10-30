using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SQLite;
using System.Windows.Input;
using System.Xml.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;

namespace AnimatedWin2dControls.Messages { }
namespace Microsoft.Xaml.Interactivity { }
namespace WinUIMusicPlayer.Behaviors { }
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static IServiceProvider Services { get; set; } = null!;
        public static Window MainWindow { get; set; } = null!;
        public static ILogger<T> GetLogger<T>() => FixtureLogger<T>.Instance;
    }
}
internal sealed class FixtureLogger<T> : ILogger<T>
{
    internal static FixtureLogger<T> Instance { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(level)) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "native-diagnostics.log"), formatter(state, error) + Environment.NewLine + error + Environment.NewLine);
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed class PlayList { public int Id { get; set; } public string Name { get; set; } = ""; }

    public static class AppSettings
    {
        public static string[] ArtistSplitters { get; } = [";", "/"];
        public static string MusicCoverCache { get; } = Path.Combine(AppContext.BaseDirectory, "covers");
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        private static readonly Dictionary<string, string> Text = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "home-resources.resw"))
            .Root!.Elements("data").ToDictionary(x => x.Attribute("name")!.Value, x => x.Element("value")!.Value);
        public static string GetString(string key) => Text.TryGetValue(key, out var value) ? value : throw new Exception("Missing resource " + key);
        public static int RawActive;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> Artwork = new();
        public static void PrepareArtwork(int id, int width = 256, int height = 256)
        {
            var bytes = new byte[54 + width * height * 4];
            bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -height);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = 54 + (y * width + x) * 4;
                    double wave = Math.Sin(x * .018 + y * .025 + id) * .5 + .5;
                    bytes[p] = (byte)(40 + (id * 51 % 130) + wave * 50);
                    bytes[p + 1] = (byte)(30 + (id * 23 % 120) + y * .15);
                    bytes[p + 2] = (byte)(45 + (id * 71 % 135) + wave * 45);
                    bytes[p + 3] = 255;
                }
            Artwork[id] = bytes;
        }
        public static void PrepareCenteredArtwork(int id)
        {
            PrepareArtwork(id);
            var bytes = Artwork[id];
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    int p = 54 + (y * 256 + x) * 4;
                    bytes[p] = (byte)(x > 170 ? 220 : 20);
                    bytes[p + 1] = (byte)(x >= 85 && x <= 170 ? 220 : 20);
                    bytes[p + 2] = (byte)(x < 85 ? 220 : 20);
                }
        }
        public static async Task<byte[]?> GetRawImage(Music music, bool unused, CancellationToken token)
        {
            Interlocked.Increment(ref RawActive);
            try { await Task.Delay(60, token); return Artwork.GetValueOrDefault(music.Id); }
            finally { Interlocked.Decrement(ref RawActive); }
        }
    }
    public static class BindUtils
    {
        public static Visibility BoolToVisibilityConverter(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        public static string FavouriteGlyphConverter(bool value) => value ? "\uEB52" : "\uEB51";
    }
}
namespace WinUIMusicPlayer.Services
{
    public static class MusicCommands
    {
        public static ICommand PlayCommand { get; } = new RelayCommand(() => { });
        public static ICommand UpdateFavouriteCommand { get; } = new RelayCommand<Music>(m =>
        {
            if (m is null) return;
            m.IsFavorite = !m.IsFavorite;
        });
        public static ICommand AddToPlayListCommand { get; } = new RelayCommand(() => { });
        public static void OnLyricsOffsetChanged(Music music, int value) { }
    }
    public partial class MusicDatabaseService
    {
        private readonly SQLiteAsyncConnection _dbConnection;
        private Task _initialized = Task.CompletedTask;
        public Task PlaylistGate { get; set; } = Task.CompletedTask;
        public bool FailPlaylistAdd { get; set; }
        public int PlaylistAddsStarted;
        public async Task AddMusicToPlayList(int playlistId, int musicId)
        {
            PlaylistAddsStarted++;
            await PlaylistGate;
            if (FailPlaylistAdd) throw new IOException("fixture playlist failure");
            if (await _dbConnection.Table<PlayListMusic>().Where(item => item.PlayListId == playlistId && item.MusicId == musicId).FirstOrDefaultAsync() is not null) return;
            var last = await _dbConnection.Table<PlayListMusic>().Where(item => item.PlayListId == playlistId).OrderByDescending(item => item.Order).FirstOrDefaultAsync();
            await _dbConnection.InsertAsync(new PlayListMusic { PlayListId = playlistId, MusicId = musicId, Order = (last?.Order ?? 0) + 1 });
        }
        public int QueriesStarted;
        public Task WhenInitialized { get { QueriesStarted++; return _initialized; } set => _initialized = value; }
        public MusicDatabaseService(SQLiteAsyncConnection database) => _dbConnection = database;
    }
    public sealed class PlaybackStatsService
    {
        public const double QualifiedRatio = .5;
        public event Action? StatsUpdated;
        public void Notify() => StatsUpdated?.Invoke();
    }
    public sealed class PlaybackCoordinator(WinUIMusicPlayer.ViewModel.AppViewModel app, PlaybackStatsService statistics)
    {
        public int LastId;
        public async Task PlayAsync(Music music)
        {
            LastId = music.Id;
            app.CurrentPlayingMusic = music;
            statistics.Notify();
            await Task.Yield();
        }
    }
    public sealed class WebDavLibraryService
    {
        public event Action<int, bool>? SourceAvailabilityChanged;
        public void Notify(int id, bool offline) => SourceAvailabilityChanged?.Invoke(id, offline);
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class FixturePreferences : ObservableObject
    {
        public void Notify() => OnPropertyChanged("ArtistSplitSymbols");
    }
    public sealed class FixtureState
    {
        public LibraryState Library { get; } = new();
        public Services.AppLifecycle Lifecycle { get; } = new();
        public FixturePreferences Preferences { get; } = new();
    }
    public sealed class AppViewModel : ObservableObject
    {
        public FixtureState State { get; } = new();
        public List<Music> SongsSource => State.Library.Songs;
        public BulkObservableCollection<Music> FavoriteSongs { get; } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; } = [];
        public BulkObservableCollection<Music> SequentialPlayingList { get; set; } = [];
        public bool IsPlaybackEngineReady { get; set => SetProperty(ref field, value); }
        public bool IsInitialized => State.Lifecycle.IsReady;
        public bool CanPublishState => State.Lifecycle.Phase != Services.AppPhase.Stopping;
        public bool CanStartPlayback => IsInitialized && IsPlaybackEngineReady;
        public Music? CurrentPlayingMusic { get; set => SetProperty(ref field, value); }
        public Visibility LibraryEmptyVisibility { get; set; } = Visibility.Collapsed;
        public event Action? SongsSourceChanged;
        public Music? FindById(int id) => SongsSource.Find(m => m.Id == id);
        public void Replace(List<Music> songs)
        {
            State.Library.Replace(songs);
            SongsSourceChanged?.Invoke();
            State.Library.NotifyChanged();
            LibraryEmptyVisibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    public sealed class AddFolderViewModel : ObservableObject
    {
        public bool IsScanning { get; set => SetProperty(ref field, value); }
        public IAsyncRelayCommand AddFolderCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public Task DropFoldersAsync(IReadOnlyList<Windows.Storage.IStorageItem> items) => Task.CompletedTask;
    }
}
