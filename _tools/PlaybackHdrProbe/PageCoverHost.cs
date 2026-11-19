using System.ComponentModel;
using AnimatedWin2dControls.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer
{
    // The probe owns a window and a single raw cover file. It never starts application services.
    internal static class App
    {
        public static Window MainWindow { get; set; } = null!;
        public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    }
}

namespace WinUIMusicPlayer.Utils
{
    internal static partial class ToolUtils
    {
        internal static string FindRawCachePath(string hash) => hash;
    }
}

namespace WinUIMusicPlayer.Model
{
    internal static class AppSettings
    {
        public static ImageSwitchType ImageSwitchType => ImageSwitchType.ScaleInOut;
    }
}

namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : ObservableObject
    {
        public string? LyricPageBackgroundHash { get; set => SetProperty(ref field, value); }
        public int BackgroundShaderIndex { get; init; }
        public double WindowBackgroundBlurAmount => 0;
        public bool IsDarkMode => true;
        public bool IsPlayingDetailVisible { get => field; set => SetProperty(ref field, value); } = true;
        public bool IsFluidBackgroundEnabled { get; set => SetProperty(ref field, value); }
        public string? WindowBackgroundImagePath { get; set => SetProperty(ref field, value); }
        public bool PlaybackHdrEnabled { get; set => SetProperty(ref field, value); }
        public bool PlaybackHdrDisplayAvailable => true;
        public double PlaybackHdrWhiteNits { get; init; }
        public double PlaybackHdrEffectivePeakNits { get; init; }
        public ulong PlaybackHdrDisplayId { get; init; }
        public bool PlaybackHdrActive { get; private set; }
        public bool PlaybackHdrFailed { get; private set; }
        public const double PortraitLyricsScale = 1;
        public int LyricsFontSize { get; set; }
        public bool IsPortraitLayout { get; set; }

        public void ReportPlaybackHdrPresentation(bool active, bool failed)
        {
            PlaybackHdrActive = active;
            PlaybackHdrFailed = failed;
        }
    }
}

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class PlayingDetailViewModel(AppViewModel appViewModel) : ObservableObject
    {
        public AppViewModel AppViewModel { get; } = appViewModel;
        public double TitleFontSize { get; set; }
        public double ArtistAlbumFontSize { get; set; }
        public double InfoFontSize { get; set; }
    }
}

namespace WinUIMusicPlayer.View
{
    public sealed partial class PlayingDetailPage : Page, IDisposable
    {
        private readonly ILogger<PlayingDetailPage> _logger = App.GetLogger<PlayingDetailPage>();
        private bool _isLoaded;
        private bool _disposed;
        private static bool IsCoverMotionEnabled => true;

        internal PlayingDetailPage(AppViewModel appViewModel)
        {
            ViewModel = new PlayingDetailViewModel(appViewModel);
            InitializeComponent();
            InitializeSceneCover();
            Loaded += (_, _) =>
            {
                _isLoaded = true;
                appViewModel.PropertyChanged += RefreshPresentation;
                ViewModel.PropertyChanged += UpdatePresentation;
                ViewModel.RefreshPresentation();
            };
        }

        public PlayingDetailViewModel ViewModel { get; }
        internal ImageSwitcher Cover => DetailCoverSwitcher;
        internal NowPlayingCanvas? BackgroundCanvas => NowPlaying;
        internal Rect CoverSlot => _sceneCoverSlot;
        internal int SceneCoverPixelWidth => _sceneCoverPresenter.RequestedArtwork?.Width ?? 0;
        internal Action<NowPlayingCanvas>? ConfigureBackgroundCanvas { get; set; }

        public void PauseCanvasRendering() => NowPlaying?.PauseRendering();
        public void ResumeCanvasRendering() => NowPlaying?.ResumeRendering();

        private void NowPlaying_Loaded(object sender, RoutedEventArgs e)
        {
            var canvas = (NowPlayingCanvas)sender;
            ConfigureBackgroundCanvas?.Invoke(canvas);
            AttachSceneCoverCanvas(canvas);
            ObserveHdrEntrance(canvas);
            _ = canvas.SetHdrNavigationTransitionAsync(_canvasNavigationTransition);
            canvas.HdrPresentationChanged += PlaybackHdrPresentationChanged;
            UpdateHdrPresentation();
        }

        private void RefreshPresentation(object? sender, PropertyChangedEventArgs e) => ViewModel.RefreshPresentation();

        private void UpdatePresentation(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayingDetailViewModel.UseHdrPresentation)) UpdateHdrPresentation();
        }

        private void ScheduleLyricsRegionUpdate() { }
        private void UpdateSceneRegions() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelHdrEntrance();
            ViewModel.AppViewModel.PropertyChanged -= RefreshPresentation;
            ViewModel.PropertyChanged -= UpdatePresentation;
            DisposeSceneCover();
            if (NowPlaying is not { } canvas) return;
            canvas.HdrPresentationChanged -= PlaybackHdrPresentationChanged;
            canvas.Dispose();
        }
    }
}
