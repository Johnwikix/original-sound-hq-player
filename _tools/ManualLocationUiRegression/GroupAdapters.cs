using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils
{
    public static class FixtureBinds
    {
        public static bool Not(bool value) => !value;
        public static double Opacity(bool detail) => detail ? 0 : 1;
        public static Visibility Detail(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    }
}

namespace WinUIMusicPlayer.ViewModel
{
    // UI fixture only: the production pages and position owner are linked verbatim.
    // Playback, menu actions and database business remain isolated.
    public abstract class GroupFixtureViewModel(AppViewModel app, string kind) : ObservableObject
    {
        public AppViewModel AppViewModel { get; } = app;
        public Music? SelectedItem { get; set; }
        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }
        private Music? Current => kind switch { "album" => app.CurrentAlbumObj, "artist" => app.CurrentArtistObj, _ => app.CurrentFolderObj };
        private void SetCurrent(Music? music)
        {
            if (kind == "album") app.CurrentAlbumObj = music;
            else if (kind == "artist") app.CurrentArtistObj = music;
            else app.CurrentFolderObj = music;
        }
        public void ReceiveNavigation() => IsInDetailMode = Current is not null;
        public void EnterDetailFromCrossLink() => ReceiveNavigation();
        public void CollapseDetail() { SetCurrent(null); IsInDetailMode = false; }
        protected void Enter(ItemClickEventArgs args) { SetCurrent((Music)args.ClickedItem); IsInDetailMode = true; }
        public void RefreshDetailView() { }
    }
    public sealed class AlbumViewModel(AppViewModel app) : GroupFixtureViewModel(app, "album")
    {
        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs args) => Enter(args);
    }
    public sealed class ArtistViewModel(AppViewModel app) : GroupFixtureViewModel(app, "artist")
    {
        public void ArtistGridView_ItemClick(object sender, ItemClickEventArgs args) => Enter(args);
    }
    public sealed class FolderViewModel(AppViewModel app) : GroupFixtureViewModel(app, "folder")
    {
        public void FolderGridView_ItemClick(object sender, ItemClickEventArgs args) => Enter(args);
    }
}

namespace WinUIMusicPlayer.View.Controls
{
    public sealed class FixtureDetailViewModel : ObservableObject
    {
        public bool IsClosingForTransition { get; set; }
        public object? Songs { get; set => SetProperty(ref field, value); }
        public void RefreshFromAppState() { }
    }
    public sealed partial class MusicGroupDetailControl : UserControl
    {
        public FixtureDetailViewModel ViewModel { get; } = new();
        public Border DetailCoverBorder => CoverBorder;
        public MusicGroupDetailControl()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                var app = App.Services.GetRequiredService<WinUIMusicPlayer.ViewModel.AppViewModel>();
                ViewModel.Songs = Tag?.ToString() switch { "album" => app.AlbumSongs, "artist" => app.ArtistSongs, _ => app.FolderSongs };
            };
        }
    }
}

namespace WinUIMusicPlayer.Helper { public static class Constants { public static class Time { public static TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(300); } } }
