using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

// Only shell chrome, the playing overlay and the editor body are substituted. All Show/Close/Navigate
// methods are linked from MainPage.LibraryNavigation.cs; the presenter, service, browser and child Frames are real.
public sealed partial class MainPage : Page
{
    private readonly Frame MainFrame = new() { CacheSize = 0 };
    private readonly Frame PlayingFrame = new() { Visibility = Visibility.Collapsed };
    private readonly NavigationView NavigationViewControl = new();
    private readonly TeachingTip CurrentPlayListTeachingTip = new();
    private bool _isPageTransitioning;
    private FixtureShellViewModel? _viewModel;
    public FixtureShellViewModel ViewModel => _viewModel ??= new(App.Services.GetRequiredService<AppViewModel>());
    public Frame ShellFrame => MainFrame;
    public MusicBrowsePage? CurrentLibraryPage => MainFrame.Content as MusicBrowsePage;
    public ContentDialog EqualizerDialog { get; } = new();
    public bool HasMusicDetails => _detailsPresenter.IsOpen;
    public int ReturnedToPlaying { get; private set; }
    public MainPage()
    {
        _detailsPresenter = new MusicDetailsPresenter(MainFrame);
        NavigationViewControl.MenuItems.Add(new NavigationViewItem { Tag = "MusicBrowse", Content = "Library" });
        Content = MainFrame;
    }
    public void NavigateToPlayingDetailPage() => ReturnedToPlaying++;
    public void NavigatebackToMusicBrowsePage() { PlayingFrame.Visibility = Visibility.Collapsed; OpenPendingDetails(); }
    public void CancelDetailsForFixture() => _detailsPresenter.Dismiss();
    public void FinishPlayingTransitionForFixture() { _isPageTransitioning = false; OpenPendingDetails(); }
    public void BeginPlayingTransitionForFixture() => _isPageTransitioning = true;
}
public sealed class FixtureShellViewModel(AppViewModel app) { public AppViewModel AppViewModel { get; } = app; }
public sealed class HomePage : Page { public void SetExternalDetailsVisible(bool value) { } }
public sealed class PlayListPage : Page { public FixturePlaylistState ViewModel { get; } = new(); }
public sealed class FixturePlaylistState { public bool IsInDetailMode { get; set; } }
public sealed class PlayingDetailPage : Page { public void CloseQueuePopup() { } }

public sealed class MusicDetailsPanel : UserControl
{
    private readonly Border _cover = new() { Width = 100, Height = 100, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue) };
    private readonly TextBlock _title = new() { Text = "Details", FontSize = 20 };
    public Music? Music { get; private set; }
    public FrameworkElement CoverElement => _cover;
    public FrameworkElement TitleElement => _title;
    public bool Open(MusicDetailsRequest request)
    {
        Music = request.Music;
        var layout = new StackPanel { Margin = new Thickness(24) };
        layout.Children.Add(_cover); layout.Children.Add(_title);
        Content = layout;
        return true;
    }
    public void UpdatePlaybackContext(MusicDetailsRequest request) { }
    public void FocusHeader() => Focus(FocusState.Programmatic);
    public void BeginClose() { }
    public void Release() { Music = null; Content = null; }
}
