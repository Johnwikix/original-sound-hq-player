using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PlayingDetailPage : Page, IDisposable
    {
        public PlayingDetailViewModel ViewModel { get; }
        public MainViewModel QueueViewModel { get; }
        private readonly Windows.UI.ViewManagement.UISettings _motionSettings = new();
        private bool IsCoverMotionEnabled => _motionSettings.AnimationsEnabled;
        private readonly ListViewCentering _queueCentering;
        private readonly PlaybackSelectionTracker _playbackSelection;
        private ILogger<PlayingDetailPage> _logger;
        public PlayingDetailPage(PlayingDetailViewModel viewModel)
        {
            ViewModel = viewModel;
            QueueViewModel = App.Services.GetRequiredService<MainViewModel>();
            this.InitializeComponent();
            InitializeHdrCover();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            DataContext = this;
            _queueCentering = new ListViewCentering(CurrentPlayListViewPlayingDetail);
            _playbackSelection = new PlaybackSelectionTracker(CurrentPlayListViewPlayingDetail, ViewModel.AppViewModel, queue: true);
            Loaded += PlayingDetailPage_Loaded;
            Unloaded += PlayingDetailPage_Unloaded;
            _logger = App.GetLogger<PlayingDetailPage>();
        }

        public void CloseQueuePopup() => CurrentPlayListTeachingTipPlayingDetail.IsOpen = false;

        private bool _isLoaded;
        private bool _disposed;
        private XamlRoot? _hdrXamlRoot;
        private double _hdrRasterizationScale;
        private bool _sceneRegionsQueued;
        public bool IsLoaded => _isLoaded;

        private void PlayingDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded || _disposed) return;
            _isLoaded = true;
            _hdrXamlRoot = XamlRoot;
            _hdrRasterizationScale = _hdrXamlRoot?.RasterizationScale ?? 1;
            if (_hdrXamlRoot is not null)
                _hdrXamlRoot.Changed += PlaybackXamlRootChanged;
            if (SupportsAnimationSettingsEvent)
                _motionSettings.AnimationsEnabledChanged += MotionSettingsChanged;
            ViewModel.PropertyChanged += Presentation_PropertyChanged;
            ViewModel.RefreshPresentation();
            ApplyAnimatedTextEffect();
            App.MainWindow.SizeChanged += MainWindow_SizeChanged;
            App.MainWindow.Activated += MainWindow_Activated;
            App.MainWindow.AppWindow.Changed += AppWindow_Changed;
            ViewModel.AppViewModel.PropertyChanged += AppViewModel_PropertyChanged;
            // 首次进入也下发：null 时着色器回退内置配色，避免沿用其它页面的残留状态
            NowPlaying?.SetPalette(ViewModel.AppViewModel.LyricPagePalette);
            NowPlaying?.SetArtwork(ViewModel.AppViewModel.LyricPageArtwork);
            UpdateLyricsRegion();
            UpdateHdrPresentation();
            UpdateHdrTitleRegion();
            UpdateWindowRendering(App.MainWindow.AppWindow);
        }

        private void PlayingDetailPage_Unloaded(object sender, RoutedEventArgs e) => DetachPresentationEvents();

        private void DetachPresentationEvents()
        {
            CancelHdrEntrance();
            if (!_isLoaded) return;
            _isLoaded = false;
            if (_hdrXamlRoot is { } root)
                root.Changed -= PlaybackXamlRootChanged;
            _hdrXamlRoot = null;
            if (SupportsAnimationSettingsEvent)
                _motionSettings.AnimationsEnabledChanged -= MotionSettingsChanged;
            ViewModel.PropertyChanged -= Presentation_PropertyChanged;
            ViewModel.AppViewModel.PropertyChanged -= AppViewModel_PropertyChanged;
            if (App.MainWindow is { } window)
            {
                window.SizeChanged -= MainWindow_SizeChanged;
                window.Activated -= MainWindow_Activated;
                window.AppWindow.Changed -= AppWindow_Changed;
            }
        }

        private void Presentation_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayingDetailViewModel.UseHdrPresentation))
                UpdateHdrPresentation();
        }

        private void PlaybackXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            UpdateWindowRendering(App.MainWindow.AppWindow);
            if (_hdrRasterizationScale != sender.RasterizationScale)
            {
                _hdrRasterizationScale = sender.RasterizationScale;
                NowPlaying?.RetryHdrPresentation();
            }
            ScheduleLyricsRegionUpdate();
        }

        private void AppViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null or ""
                or nameof(AppViewModel.IsFluidBackgroundEnabled)
                or nameof(AppViewModel.WindowBackgroundImagePath)
                or nameof(AppViewModel.PlaybackHdrEnabled)
                or nameof(AppViewModel.PlaybackHdrDisplayAvailable))
            {
                if (DispatcherQueue.HasThreadAccess)
                    ViewModel.RefreshPresentation();
                else
                    DispatcherQueue.TryEnqueue(ViewModel.RefreshPresentation);
            }

            if (e.PropertyName is nameof(AppViewModel.PlaybackHdrDisplayId) or nameof(AppViewModel.PlaybackHdrWhiteNits)
                or nameof(AppViewModel.PlaybackHdrEffectivePeakNits))
            {
                UpdateHdrPresentation();
            }
            else if (e.PropertyName == nameof(AppViewModel.IsPlayingDetailVisible))
            {
                UpdateWindowRendering(App.MainWindow.AppWindow);
            }
            else if (e.PropertyName == nameof(AppViewModel.LyricPagePalette))
            {
                // null 也下发：让着色器回退内置配色，避免滞留上一首的调色板
                NowPlaying?.SetPalette(ViewModel.AppViewModel.LyricPagePalette);
            }
            else if (e.PropertyName == nameof(AppViewModel.LyricPageArtwork))
            {
                NowPlaying?.SetArtwork(ViewModel.AppViewModel.LyricPageArtwork);
            }
            else if (e.PropertyName == nameof(AppViewModel.LyricsMargin))
            {
                UpdateLyricsRegion();
            }
            else if (e.PropertyName == nameof(AppViewModel.Win2dTextEffectType))
            {
                ApplyAnimatedTextEffect();
            }
        }

        private void AnimatedPlayingDetailTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            // Reapply the current effect after the Win2D template is ready.
            ApplyAnimatedTextEffect();
            UpdateHdrTitleRegion();
        }

        private static readonly bool SupportsAnimationSettingsEvent =
            Windows.Foundation.Metadata.ApiInformation.IsEventPresent("Windows.UI.ViewManagement.UISettings", "AnimationsEnabledChanged");

        private void MotionSettingsChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isLoaded) ApplyAnimatedTextEffect();
            });
        }

        private void ApplyAnimatedTextEffect()
        {
            var textBlock = AnimatedPlayingDetailTextBlock;
            if (textBlock is null)
                return;

            textBlock.TextEffect = _motionSettings.AnimationsEnabled
                ? CreateAnimatedTextEffect(ViewModel.AppViewModel.Win2dTextEffectType.Value)
                : null;
        }

        private static AnimatedWin2dControls.Controls.AnimatedTextBlock.ITextEffect CreateAnimatedTextEffect(AnimatedTextEffect type) => type switch
        {
            AnimatedTextEffect.TextBlurEffect => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextBlurEffect(),
            AnimatedTextEffect.TextElasticEffect => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextElasticEffect(),
            AnimatedTextEffect.TextFadeEffect => new TextFadeEffect(),
            AnimatedTextEffect.TextMotionBlurEffect => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextMotionBlurEffect(),
            AnimatedTextEffect.TextPivotEffect => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextPivotEffect(),
            AnimatedTextEffect.TextWipeEffect => new TextWipeEffect(),
            AnimatedTextEffect.TextZoomEffect => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextZoomEffect(),
            _ => new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextDefaultEffect()
        };

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidVisibilityChange || args.DidPresenterChange || args.DidSizeChange)
                UpdateWindowRendering(sender);
        }

        private void UpdateWindowRendering(AppWindow window)
        {
            bool paused = !window.IsVisible || XamlRoot is { IsHostVisible: false }
                || WindowHelper.IsIconic(AppData.HWnd);
            UpdateHdrEntranceWindowPaused(paused);
            NowPlaying?.SetWindowPaused(paused);
            LyricsView?.SetWindowPaused(paused);
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
            => UpdateWindowRendering(App.MainWindow.AppWindow);

        private void PlaybackLyrics_Loaded(object sender, RoutedEventArgs args)
            => UpdateWindowRendering(App.MainWindow.AppWindow);

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            UpdateWindowRendering(App.MainWindow.AppWindow);
            ScheduleLyricsRegionUpdate();
        }

        private void NowPlaying_Loaded(object sender, RoutedEventArgs e)
        {
            var canvas = (NowPlayingCanvas)sender;
            AttachHdrCoverCanvas(canvas);
            ObserveHdrEntrance(canvas);
            _ = canvas.SetHdrNavigationTransitionAsync(_canvasNavigationTransition);
            canvas.HdrPresentationChanged -= PlaybackHdrPresentationChanged;
            canvas.HdrPresentationChanged += PlaybackHdrPresentationChanged;

            canvas.SetPalette(ViewModel.AppViewModel.LyricPagePalette);
            canvas.SetArtwork(ViewModel.AppViewModel.LyricPageArtwork);
            UpdateWindowRendering(App.MainWindow.AppWindow);
            UpdateHdrPresentation();
            ScheduleLyricsRegionUpdate();
        }

        private void AnimatedPlayingDetailTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateHdrTitleRegion();

        private void UpdateHdrTitleRegion()
        {
            if (NowPlaying is not { IsLoaded: true } canvas || AnimatedPlayingDetailTextBlock is not { } title
                || !title.IsLoaded || title.ActualWidth <= 0 || title.ActualHeight <= 0)
                return;

            var bounds = title.TransformToVisual(canvas).TransformBounds(
                new Windows.Foundation.Rect(0, 0, title.ActualWidth, title.ActualHeight));
            canvas.SetHdrTitle(title, bounds);
        }

        private void ControlsStack_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.ControlsStackOpacity = 1.0f;
        }

        private void ControlsStack_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.ControlsStackOpacity = 0.0f;
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = false;
        }       

        private void ProgressSliderPlayingDetail_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = ToolUtils.FindVisualChild<Thumb>(ProgressSliderPlayingDetail);
            if (thumb is not null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = false;
            var (_, totalMs) = ViewModel.AppViewModel.GetTimeProgressCache();
            long newPosMs = Math.Max(0, Math.Min((long)(ViewModel.AppViewModel.ProgressSlider * 1000), totalMs));
            ViewModel.AppViewModel.IsManualSelect = true;
            try
            {
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(newPosMs);
            }
            finally { ViewModel.AppViewModel.IsManualSelect = false; }
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            _ = App.Services.GetRequiredService<MainPage>().EqualizerDialog.ShowThemedAsync(this.XamlRoot);
        }

        private void CurrentPlayListButtonPlayingDetail_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = true;
        }
        private async void LocateCurrentTrackButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CurrentPlayListTeachingTipPlayingDetail.IsOpen) return;
            int index = QueueViewModel.LocateCurrentTrack();
            if (index < 0 || index >= CurrentPlayListViewPlayingDetail.Items.Count) return;
            CurrentPlayListViewPlayingDetail.SelectedIndex = index;
            await _queueCentering.LocateIndexAsync(index);
        }

        private void CurrentPlayListTeachingTipPlayingDetail_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            // 弹层可能在拖拽未完成时关闭，提交兜底避免队列一直冻结在重排中。
            ViewModel.AppViewModel.State.Queue.EndReorder();
            _queueCentering.Cancel();
        }

        private void CurrentPlayListViewPlayingDetail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ListViewInteraction.IsAction(e.OriginalSource as DependencyObject, CurrentPlayListViewPlayingDetail)) return;
            int index = ListViewInteraction.IndexFromSource(e.OriginalSource as DependencyObject, CurrentPlayListViewPlayingDetail);
            if (index < 0 || index >= CurrentPlayListViewPlayingDetail.Items.Count) return;
            e.Handled = true;
            _ = App.Services.GetRequiredService<PlaybackCoordinator>().PlayAtAsync(index);
        }

        private void CurrentPlayListViewPlayingDetail_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
            => ViewModel.AppViewModel.State.Queue.BeginReorder();

        private void CurrentPlayListViewPlayingDetail_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
            => ViewModel.AppViewModel.State.Queue.EndReorder();

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void VolumeButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(VolumePlayingDetailBtn).Properties.MouseWheelDelta;
            if (delta > 0)
            {
                ViewModel.AppViewModel.AdjustVolume(5);
            }
            else if (delta < 0)
            {
                ViewModel.AppViewModel.AdjustVolume(-5);
            }
            e.Handled = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void CurrentPlayListTeachingTipPlayingDetailCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = false;
        }

        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSliderPlayingDetail).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    ViewModel.AppViewModel.AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void PauseCanvasRendering()
        {
            AnimatedPlayingDetailTextBlock.IsActive = false;
            NowPlaying?.PauseRendering();
            LyricsView?.PauseRendering();
        }

        public void ResumeCanvasRendering()
        {
            AnimatedPlayingDetailTextBlock.IsActive = true;
            NowPlaying?.ResumeRendering();
            LyricsView?.ResumeRendering();
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                // 页面先于队列弹层关闭时也要提交拖拽，避免队列冻结在重排中。
                ViewModel?.AppViewModel?.State.Queue.EndReorder();
                DetachPresentationEvents();
                DisposeHdrCover();
                LyricsView?.LyricLineClicked -= LyricsView_LyricInteracted;
                LyricsView?.RenderError -= LyricsView_ExceptionInteracted;
                LyricsView?.PrepareForShutdown();
                NowPlaying?.Dispose();
                AnimatedPlayingDetailTextBlock?.PrepareForShutdown();
            }
        }

        private void BackGround_ThemeResolved(object sender, bool isDark)
        {
            App.Services.GetRequiredService<AppViewModel>().IsDarkMode = isDark;
            if (isDark)
            {
                AppSettings.AppTheme = "Dark";
                AppSettings.ElementTheme = ElementTheme.Dark;

            }
            else
            {
                AppSettings.AppTheme = "Light";
                AppSettings.ElementTheme = ElementTheme.Light;
            }
            App.MainWindow?.SetAppTheme();
        }

        private void LyricsView_LyricInteracted(object? sender, TimeSpan e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.LyricLine? lyricLine = ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.StartMs >= e.TotalMilliseconds);
                ViewModel.AppViewModel.IsManualSelect = true;
                if (lyricLine is not null)
                {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(lyricLine);
                    ViewModel.UpdateLyricsToUI(index);
                }
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime((long)e.TotalMilliseconds);
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }

        private void LyricsView_ExceptionInteracted(object? sender, Exception e)
        {
            _logger.LogError(e, "歌词渲染错误");
        }

        private void BackGround_ExceptionOccurred(object? sender, Exception e)
        {
            _logger.LogError(e, "背景渲染错误");
        }

        private void LyricsRegionHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLyricsRegion();
        }

        private void ScheduleLyricsRegionUpdate()
        {
            if (!_sceneRegionsQueued)
                _sceneRegionsQueued = DispatcherQueue.TryEnqueue(UpdateSceneRegions);
        }

        private void UpdateSceneRegions()
        {
            _sceneRegionsQueued = false;
            if (!_isLoaded || _disposed) return;
            UpdateLyricsRegion();
            UpdateHdrTitleRegion();
        }

        private void UpdateLyricsRegion()
        {
            if (NowPlaying is null || LyricsRegionHost is null) return;
            if (LyricsRegionHost.ActualWidth <= 0 || LyricsRegionHost.ActualHeight <= 0) return;
            try
            {
                var transform = LyricsRegionHost.TransformToVisual(NowPlaying);
                var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var margin = ViewModel.AppViewModel.LyricsMargin;
                NowPlaying.LyricsRegion = new Windows.Foundation.Rect(
                    origin.X + margin.Left,
                    origin.Y + 32,
                    LyricsRegionHost.ActualWidth - margin.Left - margin.Right,
                    LyricsRegionHost.ActualHeight - 32);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "更新歌词区域时发生错误:{StackTrace}",ex.StackTrace);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _ = App.Services.GetRequiredService<MainPage>().SettingsDialog.ShowThemedAsync(this.XamlRoot);
        }
    }
}
