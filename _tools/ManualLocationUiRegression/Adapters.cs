using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.Collections.ObjectModel;
using System.Windows.Input;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;

// These adapters replace playback, disk I/O, cover decoding and unrelated shell chrome.
// The location path uses actual production ViewModels, pages, bindings, bulk collections,
// navigation boundary and WinUI virtualization. No adapter scrolls or dispatches location.
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static IServiceProvider Services = null!;
        public static Window MainWindow = null!;
        public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    }
}
namespace WinUIMusicPlayer.State { public sealed class AppState { public PlaybackQueueState Queue { get; } = new(); public BrowseState Browse { get; } = new(); } }
namespace AnimatedWin2dControls.Messages { }
namespace WinUIMusicPlayer.Reader { public static class Placeholder { } }
namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Album { get; set; } = "Album";
        public string Author { get; set; } = "Artist";
        public string Path { get; set; } = "";
        public string LastLevelFolderPath { get; set; } = "Folder";
        public bool IsFavorite { get; set; }
        public int Order { get; set; }
    }
    public sealed class PlayListMusicItem { public Music Music { get; set; } = null!; }
    public sealed class PlayList { public int Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class SortOption { public string Tag { get; set; } = "DefaultOrder"; public string DisplayText { get; set; } = ""; public string UidKey { get; set; } = ""; }
    public sealed class UsbSendTarget { public object? Device { get; set; } public string Format { get; set; } = ""; public int BitrateKbps { get; set; } }
    public enum PlayMode { SingleLoop, ListLoop, RandomLoop, RepeatOff }
    public static class AppData { public static Type CurrentPage { get; set; } = typeof(SongListPage); }
}
namespace WinUIMusicPlayer.DesktopLyrics { public sealed class DesktopLyricsViewModel { } }
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService
    {
        public Task AddMusicListToPlayList(IEnumerable<Music> songs, int id) => Task.CompletedTask;
        public Task SaveFavoriteOrderAsync(IEnumerable<Music> songs) => Task.CompletedTask;
        public Task AddToFavourite(Music music) => Task.CompletedTask;
        public Task UpdateLyricsOffsetAsync(int id, int value) => Task.CompletedTask;
        public Task RemoveMusic(int id) => Task.CompletedTask;
    }
    public sealed class BassPlayerCommandService { public void UpdateSettings() { } }
    public sealed class SystemMediaControlsService { }
    public sealed class UsbDeviceService { }
    public sealed class CoverPresentationService
    {
        public Task UpdatePlayBar(Music music, CancellationToken token = default) => Task.CompletedTask;
        public void ThemeChangedUpdateCover() { }
    }
    public sealed class PlaybackCoordinator
    {
        public event Action<Music, CancellationToken>? TrackStarted;
        public Music? LastMusic;
        public long LastEntryId;
        public Task PlayAsync(Music music, long entryId = 0)
        {
            LastMusic = music;
            LastEntryId = entryId;
            TrackStarted?.Invoke(music, default);
            return Task.CompletedTask;
        }
    }
    public sealed class PlaybackCommands
    {
        public IRelayCommand ToggleCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand NextCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand PreviousCommand { get; } = new RelayCommand(() => { });
    }
    public sealed class ApplicationTasks { public Task RunCommittedAsync(Func<Task> operation) => operation(); }
    public static class OffsetMsBus { public static void Publish(int value) { } }
}
namespace WinUIMusicPlayer.Services.NavigationService { public interface INavigatable { void ReceiveNavigationParameter(object parameter); } }
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : ObservableObject
    {
        public WinUIMusicPlayer.State.AppState State { get; } = new();
        public BulkObservableCollection<Music> ListSongs { get; } = [];
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; } = [];
        public bool IsInitialized => false;
        public void AddToFavoriteSongs(Music song) => FavoriteSongs.Add(song);
        public void RemoveFromFavoriteSongs(Music song) => FavoriteSongs.Remove(song);
        public void RemoveFromPlayListSongs(Music song) { }
        public void AddMusicToCurrentPlayList(Music song) => CurrentPlayingList.Add(song);
        public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
        public WinUIMusicPlayer.State.PlaybackQueueState Queue => State.Queue;
        public long PendingEntryId { get; set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(SelectedPlaybackMusic)); } }
        public BulkObservableCollection<Music> CurrentPlayingList => Queue.Playing;
        public int GetSelectedPlaybackIndex()
        {
            if (PendingEntryId != 0)
            {
                for (int i = 0; i < CurrentPlayingList.Count; i++)
                    if (Queue.EntryIdAt(i) == PendingEntryId) return i;
                return -1;
            }
            return Queue.IndexOf(SelectedPlaybackMusic);
        }
        public BulkObservableCollection<Music> SequentialPlayingList { get; set; } = [];
        public List<Music> SongsSource { get; } = [];
        public ObservableCollection<PlayList> AllPlayList { get; } = [];
        public ObservableCollection<SortOption> SortOptions { get; } = [];
        public SortOption SelectedSortOption { get; } = new();
        public Music? SelectedPlaybackMusic { get; set => SetProperty(ref field, value); }
        public Music? CurrentPlayingMusic => SelectedPlaybackMusic;
        public Music? CurrentAlbumObj { get; set; }
        public Music? CurrentArtistObj { get; set; }
        public Music? CurrentFolderObj { get; set; }
        public bool IsBackBtnEnable { get; set; }
        public bool CanStartPlayback => true;
        public string PageType { get; set; } = "song";
        public Microsoft.UI.Xaml.Data.CollectionViewSource AlbumPageSource { get; } = new() { IsSourceGrouped = true };
        public Microsoft.UI.Xaml.Data.CollectionViewSource ArtistPageSource { get; } = new() { IsSourceGrouped = true };
        public Microsoft.UI.Xaml.Data.CollectionViewSource FolderPageSource { get; } = new() { IsSourceGrouped = true };
        public BulkObservableCollection<Music> AlbumSongs { get; } = [];
        public BulkObservableCollection<Music> ArtistSongs { get; } = [];
        public BulkObservableCollection<Music> FolderSongs { get; } = [];
        public string DefaultPlayListComboBoxTag { get; set; } = "song";
        public PlayMode CurrentPlayMode { get; set; }
        public Visibility FavoriteEmptyVisibility => Visibility.Collapsed;
        public List<object> UILyrics { get; set; } = [];
        public void RefreshDataSource() { }
        public void LoadLyricsToUI(Music song) { }
        public Music? FindFirstByAlbum(string name) => SongsSource.FirstOrDefault(m => m.Album == name);
        public Music? FindFirstByArtist(string name) => SongsSource.FirstOrDefault(m => m.Author == name);
        public void RemoveFromSongsSource(Music song) => throw new NotSupportedException("No disk writes in fixture");
        public void AddMusicRangeToCurrentPlayList(IEnumerable<Music> songs) { }
        public Task ReGetLyrics(IEnumerable<Music> songs, Music selected) => Task.CompletedTask;
        public Task TransmitFileToUsb(IEnumerable<Music> songs, object device, string format, int bitrate) => Task.CompletedTask;
    }
    public sealed class AddFolderViewModel : ObservableObject
    {
        public bool IsScanning => false;
        public Task AddFolderWithLoadingAsync() => Task.CompletedTask;
        public Task DropFoldersAsync(IReadOnlyList<Windows.Storage.IStorageItem> items) => Task.CompletedTask;
    }
    public sealed class WebDavSourcesViewModel { }
    public sealed class AudioConversionViewModel { public Task ConvertAsync(IEnumerable<Music> music, string? tag) => Task.CompletedTask; }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ArtistHelper { public static string[] GetArtistNames(string value) => value.Split(';'); }
    public static class ToolUtils
    {
        public static T? FindVisualChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typed && match(typed)) return typed;
                if (FindVisualChild(child, match) is { } found) return found;
            }
            return null;
        }
        public static string GetString(string key) => key;
        public static bool DeleteFileFromDisk(string path) => throw new NotSupportedException("No disk writes in fixture");
        public static ObservableCollection<MenuModel> BuildConvertMenuChildren(ICommand command) => [];
        public static void UpdateMusicMenuAvailability(IEnumerable<MenuModel> menus, List<Music> selected, Music song) { }
        public static void UpdateUsbSendMenu(IEnumerable<MenuModel> menus, ICommand command) { }
    }
}
namespace WinUIMusicPlayer.Helper
{
    public static class DialogHelper
    {
        public static Task<bool> ShowConfirmAsync(XamlRoot root, string key) => Task.FromResult(false);
        public static Task<string?> ShowArtistPickerAsync(XamlRoot root, IReadOnlyList<string> names) => Task.FromResult<string?>(names[0]);
    }
}
namespace DevWinUI { public static class DialogExtensions { public static Task ShowThemedAsync(this ContentDialog dialog, XamlRoot root) => Task.CompletedTask; } }
namespace WinUIMusicPlayer.View.SubView { public sealed class MusicDetailsWindow(Music music) : Window { } }
namespace WinUIMusicPlayer.View
{
    public sealed class MainPage : Page
    {
        public Frame ShellFrame { get; set; } = new() { IsNavigationStackEnabled = false, CacheSize = 0 };
        public MusicBrowsePage? CurrentLibraryPage => ShellFrame.Content as MusicBrowsePage;
        public ContentDialog EqualizerDialog { get; } = new();
        public MainPage() => Content = ShellFrame;
        public void NavigateToMusicBrowsePage()
        {
            if (CurrentLibraryPage is null) ShellFrame.Navigate(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
        }
        public MusicDetailsRequest? LastDetailsRequest;
        public void ShowMusicDetails(MusicDetailsRequest request, FrameworkElement? sourceCover = null, FrameworkElement? sourceTitle = null) => LastDetailsRequest = request;
        public void CloseMusicDetails() { }
        public void NavigateToPlayingDetailPage() { }
    }
    public sealed partial class EmptyPage : Page { public EmptyPage() => InitializeComponent(); }
}
namespace WinUIMusicPlayer.View.Controls
{
    public sealed class MusicListRowControl : ContentControl
    {
        public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(nameof(Music), typeof(Music), typeof(MusicListRowControl), new PropertyMetadata(null, (owner, args) =>
        {
            var row = (MusicListRowControl)owner;
            var music = (Music)args.NewValue;
            row.Content = music.Title;
            row.Height = music.Id % 3 == 0 ? 76 : 44;
        }));
        public Music Music { get => (Music)GetValue(MusicProperty); set => SetValue(MusicProperty, value); }
        public FrameworkElement CoverElement => this;
        public FrameworkElement TitleElement => this;
        public event EventHandler<Music>? SongInvoked;
        public event EventHandler<Music>? ArtistInvoked;
        public event EventHandler<Music>? AlbumInvoked;
    }
}

namespace WinUIMusicPlayer.View { public sealed class PlayListPage : Page { } }
