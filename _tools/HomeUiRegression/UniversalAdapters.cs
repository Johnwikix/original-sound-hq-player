using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Behaviors
{
    // Artwork I/O is separate from the row input under test.
    public sealed class AlbumCoverBehavior : Behavior<Image>
    {
        public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(nameof(Music), typeof(Music),
            typeof(AlbumCoverBehavior), new PropertyMetadata(null));
        public Music? Music { get => (Music?)GetValue(MusicProperty); set => SetValue(MusicProperty, value); }
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class LibraryNavigationService
    {
        public Action<Music, FrameworkElement?, FrameworkElement?>? Open;
        public Action? Close;
        public void ShowSongDetails(Music music, FrameworkElement? sourceCover = null, FrameworkElement? sourceTitle = null) => Open?.Invoke(music, sourceCover, sourceTitle);
        public void CloseSongDetails() => Close?.Invoke();
    }
}
namespace WinUIMusicPlayer.View
{
    public sealed partial class UniversalOriginPage : Page
    {
        public static Music[] Songs = [];
        public ListView List { get; } = new();
        public UniversalOriginPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            foreach (var music in Songs)
            {
                var row = new WinUIMusicPlayer.View.Controls.MusicListRowControl { Music = music };
                row.SongInvoked += static (_, song) => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<WinUIMusicPlayer.Services.LibraryNavigationService>(App.Services).ShowSongDetails(song, ((WinUIMusicPlayer.View.Controls.MusicListRowControl)_!).CoverElement, ((WinUIMusicPlayer.View.Controls.MusicListRowControl)_!).TitleElement);
                List.Items.Add(row);
            }
            MusicDetailsSource.Content = List;
        }
    }
}

namespace WinUIMusicPlayer.Model { public sealed class PlayListMusicItem { public Music Music { get; set; } = null!; } }
