using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer.Model;

// Only unrelated navigation, file I/O and application services are substituted.
// The card XAML, observable playlist, projection, index and image behavior are production files.
namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public int Id { get; set; }
        public string Album { get; set; } = "";
        public string Author { get; set; } = "";
        public string LastLevelFolderPath { get; set; } = "";
        public int SourceId { get; set; }
        public bool IsRemote => SourceId != 0;
        public ImageSource? Cover { get; set; }
    }
    public static class AppData { public static Type? CurrentPage { get; set; } }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel
    {
        public ObservableCollection<PlayList> AllPlayList { get; } = [];
        public string PageType { get; set; } = "";
        public PlayList? CurrentPlayList { get; set; }
        public int CurrentPlayListId { get; set; }
    }
    public sealed class PlayListViewModel
    {
        public AppViewModel AppViewModel { get; } = new();
        public bool IsInDetailMode => false;
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static bool BoolToBoolReConverter(bool value) => !value;
        public static double BoolToOpacityReConverter(bool value) => value ? 0 : 1;
        public static double BoolToOpacityConverter(bool value) => value ? 1 : 0;
        // Used only when comparing the original production XAML: data arrives after initial binding.
        public static Music? PlayListCoverMusicConverter(int id) => App.LegacyCoverLookup?.Invoke(id);
    }
    public static class CoverLoadQueue
    {
        public static async Task<ImageSource> EnqueueAsync(Music music, CancellationToken token)
        {
            await Task.Delay(20, token);
            return music.Cover!;
        }
    }
    public static class ArtistHelper
    {
        public static string[] GetArtistNames(string name) => [name];
        public static Music CreateArtistTile(Music music, string name) => music;
    }
}
namespace WinUIMusicPlayer.View.Controls
{
    public sealed class PlaylistDetailControl : UserControl { public string? PlaySourceTag { get; set; } }
}
namespace WinUIMusicPlayer.View
{
    public sealed partial class PlayListPage : Page
    {
        public ViewModel.PlayListViewModel ViewModel { get; } = new();
        public GridView TestGrid => PlayListGridView;
        public PlayListPage() => InitializeComponent();
        private void AddPlayList_Click(object sender, RoutedEventArgs e) { }
        private void PlayListGridView_ItemClick(object sender, ItemClickEventArgs e) { }
        private void PlayListGridView_RightTapped(object sender, RightTappedRoutedEventArgs e) { }
        private void OnCoverPointerEntered(object sender, PointerRoutedEventArgs e) { }
        private void OnCoverPointerExited(object sender, PointerRoutedEventArgs e) { }
        private void PlayPlayList_Click(object sender, RoutedEventArgs e) { }
        private void EditPlayListNameButton_Click(object sender, RoutedEventArgs e) { }
        private void ExportPlayList_Click(object sender, RoutedEventArgs e) { }
        private void RemovePlayListButton_Click(object sender, RoutedEventArgs e) { }
    }
}
namespace WinUIMusicPlayer.Converters { }
namespace WinUIMusicPlayer.Helper { }
namespace CommunityToolkit.WinUI { }
