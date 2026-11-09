using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer
{
    public static class App
    {
        public static IServiceProvider Services { get; set; } = null!;
        public static Microsoft.Extensions.Logging.ILogger<T> GetLogger<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class AppData { public static Type CurrentPage { get; set; } = typeof(object); }
    public static class ToolUtils
    {
        private static readonly Dictionary<string, string> Strings = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "browser-resources.resw"))
            .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
        public static string GetString(string key) => Strings.GetValueOrDefault(key, key);
    }
    public static class BindUtils
    {
        public static Visibility BoolToVisibilityConverter(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        public static Thickness FolderRowIndent(int depth) => new(depth * 20, 0, 0, 0);
        public static string FolderChevronGlyph(bool isExpanded) => isExpanded ? "\uE70D" : "\uE76C";
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed class Music { public int SourceId { get; set; } public string FolderPath { get; set; } = ""; public string LastLevelFolderPath { get; set; } = ""; public string Title { get; set; } = ""; }
    public sealed class Folder
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int SongCount { get; set; }
        public bool IsExternalImport { get; set; }
        public bool CanOpenInExplorer => !IsExternalImport;
        public IRelayCommand OpenFolderCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand RescanFolderCommand { get; } = new RelayCommand(() => { });
        public IRelayCommand RemoveFolderCommand { get; } = new RelayCommand(() => { });
    }
    public sealed class SortOption { public string DisplayText { get; set; } = "Default"; }
}
namespace WinUIMusicPlayer.State
{
    public sealed class BrowseState : ObservableObject
    {
        public int SourceFilterId { get; set => SetProperty(ref field, value); } = -1;
        public FolderLocation? CurrentFolder { get; set => SetProperty(ref field, value); }
    }
    public sealed class AppState { public BrowseState Browse { get; } = new(); public LibraryState Library { get; } = new(); }
}
namespace WinUIMusicPlayer.Services
{
    public static class LibraryQueries
    {
        public static bool MatchesSource(Music music, int id) => id == -1 || (id == -2 ? music.SourceId != 0 : music.SourceId == id);
    }
    public sealed class MusicDatabaseService
    {
        public List<Folder> Roots { get; set; } = [];
        public TaskCompletionSource<List<Folder>>? Pending { get; set; }
        public bool Fail { get; set; }
        public async Task<List<Folder>> GetFolders()
        {
            await Task.Yield();
            if (Pending is { } pending) return await pending.Task;
            await Task.Delay(20);
            if (Fail) throw new IOException("Read failed");
            return new(Roots);
        }
    }
    public enum SongViewType { Folder }
    public sealed class LibraryProjectionService(ViewModel.AppViewModel app)
    {
        public event Action? RefreshRequested;
        private int _version;
        public void RequestRefresh() { app.State.Library.NotifyChanged(); RefreshRequested?.Invoke(); }
        public void ClearSongCollection(BulkObservableCollection<Music> target) => target.Clear();
        // 保留真实边界的 UI 线程异步发布和迟到结果丢弃；不模拟生产排序/IPC。
        public async Task UpdateSongCollectionsAsync(BulkObservableCollection<Music> target, SongViewType kind, Func<Music, bool> filter)
        {
            int version = ++_version;
            await Task.Delay(35);
            if (version != _version) return;
            var songs = app.SongsSource.Where(filter).ToArray();
            target.FillFrom(songs);
        }
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : ObservableObject
    {
        public AppViewModel() => SelectedSortOption = SortOptions[0];
        public State.AppState State { get; } = new();
        public List<Music> SongsSource => State.Library.Songs;
        public BulkObservableCollection<Music> FolderSongs { get; } = [];
        public ObservableCollection<SortOption> SortOptions { get; } = [new()];
        public SortOption SelectedSortOption { get; set => SetProperty(ref field, value); } = new();
        public string PageType { get; set; } = "";
        public bool IsBackBtnEnable { get; set; }
        public bool InfoBarIsOpen { get; set; }
        public string InfoBarTitle { get; set; } = "";
        public string InfoBarMessage { get; set; } = "";
    }
    public sealed record SourceChoice(int Id, string Name);
    public sealed class RemoteSources
    {
        public RemoteSources() => SelectedSource = Choices[0];
        public ObservableCollection<SourceChoice> Choices { get; } = [new(-1, "All sources"), new(0, "Local")];
        public SourceChoice SelectedSource { get; set; } = new(-1, "All sources");
    }
    public sealed class MusicBrowseViewModel
    {
        public RemoteSources RemoteSources { get; } = new();
        public IRelayCommand EmptyAddFolderCommand { get; } = new RelayCommand(() => { });
    }
}
namespace WinUIMusicPlayer.View.Controls
{
    // 页面集成测试仅替换歌曲控件；生产控件的播放/菜单逻辑由主项目编译及现有回归覆盖。
    public sealed partial class MusicGroupDetailControl : UserControl
    {
        public SongCommands ViewModel { get; } = new();
        public string DetailKind { get; set; } = "";
        public string PlaySourceTag { get; set; } = "";
        public bool ShowHeader { get; set; }
        public MusicGroupDetailControl()
        {
            var app = App.Services.GetRequiredService<ViewModel.AppViewModel>();
            Content = new ListView { ItemsSource = app.FolderSongs, DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        }
    }
    public sealed class SongCommands { public IRelayCommand PlayAllCommand { get; } = new RelayCommand(() => { }); }
}
