using System.ComponentModel;
using AnimatedWin2dControls.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Helper;
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

namespace WinUIMusicPlayer.Helper
{
    internal static class CoverLoadQueue
    {
        internal const int CoverSize = 150;
        internal static string GetThumbCachePath(string hash, int size) => hash + ".thumb";
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
        public object? DisplayedPlaybackMusic { get; set => SetProperty(ref field, value); }
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
        public bool IsPortraitLayout { get; set => SetProperty(ref field, value); }
        public bool IsFanLyricsEnabled { get; set => SetProperty(ref field, value); }
        public bool Is3DLyricsEnabled { get; set => SetProperty(ref field, value); }
        public bool IsAuto3DLyricsEnabled { get; set => SetProperty(ref field, value); }
        public Thickness LyricsMargin { get; set => SetProperty(ref field, value); }

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
        private readonly PointerEventHandler _parallaxPointerMoved;
        private readonly PointerEventHandler _parallaxPointerExited;
        private static bool IsCoverMotionEnabled => true;

        internal PlayingDetailPage(AppViewModel appViewModel)
        {
            ViewModel = new PlayingDetailViewModel(appViewModel);
            _parallaxPointerMoved = PlaybackParallaxPointerMoved;
            _parallaxPointerExited = PlaybackParallaxPointerExited;
            InitializeComponent();
            InitializeSceneCover();
            Loaded += (_, _) =>
            {
                _isLoaded = true;
                AddHandler(PointerMovedEvent, _parallaxPointerMoved, true);
                AddHandler(PointerExitedEvent, _parallaxPointerExited, true);
                appViewModel.PropertyChanged += RefreshPresentation;
                ViewModel.PropertyChanged += UpdatePresentation;
                ViewModel.RefreshPresentation();
            };
        }

        public PlayingDetailViewModel ViewModel { get; }
        internal ImageSwitcher Cover => DetailCoverSwitcher;
        internal AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.AdvanceLyricsCanvasControl StandaloneLyrics => LyricsView;
        internal Rect LyricsPane => LyricsRegionHost.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, LyricsRegionHost.ActualWidth, LyricsRegionHost.ActualHeight));
        internal NowPlayingCanvas? BackgroundCanvas => NowPlaying;
        internal Rect CoverSlot => _sceneCoverSlot;
        internal PlaybackSceneCoverPresenter SceneCoverPresenter => _sceneCoverPresenter;
        internal int SceneCoverPixelWidth => _sceneCoverPresenter.RequestedArtwork?.Width ?? 0;
        internal Action<NowPlayingCanvas>? ConfigureBackgroundCanvas { get; set; }
        internal AnimatedWin2dControls.Controls.AnimatedTextBlock.AnimatedTextBlock? ProbeTitle { get; set; }

        public void PauseCanvasRendering()
        {
            if (ProbeTitle is { } title) title.IsActive = false;
            NowPlaying?.PauseRendering();
            SetTitleScenePreparation(false);
            StopSceneCoverRendering();
        }

        public void ResumeCanvasRendering(bool prepareHdr = false)
        {
            SetTitleScenePreparation(prepareHdr);
            if (ProbeTitle is { } title) title.IsActive = true;
            NowPlaying?.ResumeRendering();
        }

        private void SetTitleScenePreparation(bool preparing) => ProbeTitle?.PrepareScenePresentation(preparing);

        private void NowPlaying_Loaded(object sender, RoutedEventArgs e)
        {
            var canvas = (NowPlayingCanvas)sender;
            ConfigureBackgroundCanvas?.Invoke(canvas);
            AttachSceneCoverCanvas(canvas);
            ObserveHdrEntrance(canvas);
            _ = canvas.SetHdrNavigationTransitionAsync(_canvasNavigationTransition);
            canvas.HdrPresentationChanged += PlaybackHdrPresentationChanged;
            UpdateHdrPresentation();
            ScheduleLyricsRegionUpdate();
        }

        private void RefreshPresentation(object? sender, PropertyChangedEventArgs e) => ViewModel.RefreshPresentation();

        private void UpdatePresentation(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayingDetailViewModel.UseHdrPresentation)) UpdateHdrPresentation();
            else if (e.PropertyName == nameof(PlayingDetailViewModel.UseAcrylicLyricsPresentation)) ScheduleLyricsRegionUpdate();
        }

        private void ScheduleLyricsRegionUpdate()
            => DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStandaloneLyricsRegion();
                if (NowPlaying is { } canvas && LyricsRegionHost.ActualWidth > 0)
                    canvas.LyricsRegion = LyricsRegionHost.TransformToVisual(canvas).TransformBounds(
                        new Rect(0, 0, LyricsRegionHost.ActualWidth, LyricsRegionHost.ActualHeight));
            });
        private void LyricsRegionHost_SizeChanged(object sender, SizeChangedEventArgs args) => ScheduleLyricsRegionUpdate();
        private void PlaybackLyrics_Loaded(object sender, RoutedEventArgs args) => ScheduleLyricsRegionUpdate();
        private void UpdateSceneRegions() { }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RemoveHandler(PointerMovedEvent, _parallaxPointerMoved);
            RemoveHandler(PointerExitedEvent, _parallaxPointerExited);
            SetLyricsParallaxPointer(null);
            CancelHdrEntrance();
            ViewModel.AppViewModel.PropertyChanged -= RefreshPresentation;
            ViewModel.PropertyChanged -= UpdatePresentation;
            DisposeSceneCover();
            LyricsView?.PrepareForShutdown();
            if (NowPlaying is not { } canvas) return;
            canvas.HdrPresentationChanged -= PlaybackHdrPresentationChanged;
            canvas.Dispose();
        }
    }
}
