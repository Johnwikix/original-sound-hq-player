using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;
using ZLinq;
using CommunityToolkit.WinUI;

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
        private ILogger<PlayingDetailPage> _logger;
        private float _dpiScale = 1.0f;
        private bool _isPortraitLayout;
        private bool _isPortraitWanted;
        private const double PortraitEnterRatio = 1.15;
        private const double PortraitExitRatio = 1.10;
        private const double PortraitTopVerticalMargin = 20;
        private const double textScale = 1.5;  
        private const double lyricsScale = 1.5;
        public PlayingDetailPage(PlayingDetailViewModel viewModel)
        {
            AnimatedWin2dControls.Controls.AlbumImgControl.AlbumArtControl.CoverCacheBasePath = AppSettings.MusicCoverCache;
            this.InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            Loaded += PlayingDetailPage_Loaded;
            _logger = App.GetLogger<PlayingDetailPage>();
        }

        private bool _isLoaded;
        private bool _disposed;
        public bool IsLoaded => _isLoaded;

        private void PlayingDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            if (ViewModel.AppViewModel.IsWin2dAnimatedText)
            {
                var effectType = ViewModel.AppViewModel.Win2dTextEffectType.Value;
                // 1. 定义一个简单的局部函数或直接在表达式中实例化
                AnimatedWin2dControls.Controls.AnimatedTextBlock.ITextEffect CreateEffect(AnimatedTextEffect type) => type switch
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
                var effect = CreateEffect(effectType);
                AnimatedPlayingDetailTitleTextBlock?.TextEffect = effect;
                AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = effect;
            }
            App.MainWindow.SizeChanged += MainWindow_SizeChanged;
            App.MainWindow.AppWindow.Changed += AppWindow_Changed;
            ViewModel.AppViewModel.PropertyChanged += AppViewModel_PropertyChanged;
            _ = ChangeControlsFontSize();
            if (ViewModel.AppViewModel.LyricPagePalette is { } palette)
                NowPlaying?.SetPalette(palette);
            UpdateLyricsRegion();
            Loaded -= PlayingDetailPage_Loaded;
        }

        private void AppViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.LyricPagePalette))
            {
                if (ViewModel.AppViewModel.LyricPagePalette is { } palette)
                {
                    NowPlaying?.SetPalette(palette);
                }
            }
            else if (e.PropertyName == nameof(AppViewModel.LyricsMargin))
            {
                UpdateLyricsRegion();
            }
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidVisibilityChange)
            {
                bool visible = sender.IsVisible;
                NowPlaying?.SetWindowPaused(!visible);
                LyricsView?.SetWindowPaused(!visible);
            }
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            _ = ChangeControlsFontSize();
        }

        private async Task ChangeControlsFontSize()
        {
            var windowSize = App.MainWindow.AppWindow.Size;
            _dpiScale = WindowSizeHelper.GetScaleFactor(AppData.HWnd);

            bool wantPortrait = _isPortraitWanted;
            if (windowSize.Width > 0)
            {
                double ratio = (double)windowSize.Height / windowSize.Width;
                wantPortrait = _isPortraitWanted
                    ? ratio > PortraitExitRatio
                    : ratio > PortraitEnterRatio;
            }

            bool portrait = wantPortrait;
            double driver = windowSize.Width / _dpiScale;
            var (lyrics, title, artist,info, topHeight) = driver switch
            {
                <= 1024 => (36, 30, 24, 10,  260),
                < 1280 => (42, 36, 26, 11,  280),
                < 1600 => (52, 44, 30, 13, 300),
                < 1920 => (64, 56, 34,  14, 320),
                < 2560 => (80, 64, 38, 15, 340),
                < 2880 => (96, 72, 40, 16, 360),
                _ => (120, 86, 44, 18, 380)
            };

            if (portrait)
            { 
                lyrics = (int)(lyrics * lyricsScale);
                title = (int)(title * textScale);
                artist = (int)(artist * textScale);
                info = (int)(info * textScale);
            }

            // 4. 应用变更
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                ViewModel.TitleFontSize = title;
                ViewModel.ArtistAlbumFontSize = artist;
                ViewModel.InfoFontSize = info;
                ViewModel.AppViewModel.LyricsFontSize = lyrics;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = title;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = artist;

                if (windowSize.Width > 0 && wantPortrait != _isPortraitWanted)
                {
                    _isPortraitWanted = wantPortrait;
                    ApplyAspectRatioLayout(wantPortrait, topHeight);
                    UpdateLyricsRegion();
                }
            });
        }

        private void ApplyAspectRatioLayout(bool portrait, double topHeight = 300)
        {
            if (_isPortraitLayout == portrait) return;
            _isPortraitLayout = portrait;

            if (portrait)
            {
                // Z-order: LyricsRegionHost 最底层，LeftControlPanel 浮在上面（不用 Remove/Insert 避免 Unloaded 触发 PrepareForShutdown）
                Canvas.SetZIndex(LyricsRegionHost, 0);
                Canvas.SetZIndex(LeftControlPanel, 2);

                PlayingDetail.RowDefinitions.Clear();
                PlayingDetail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayingDetail.ColumnDefinitions.Clear();
                PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // LyricsRegionHost 占满全页
                Grid.SetRow(LyricsRegionHost, 0);
                Grid.SetColumn(LyricsRegionHost, 0);
                Grid.SetRowSpan(LyricsRegionHost, 1);
                Grid.SetColumnSpan(LyricsRegionHost, 1);
                LyricsRegionHost.Margin = new Thickness(0,60,0,0);

                // LeftControlPanel 固定在顶部，浮在 lyrics 之上
                LeftControlPanel.Height = topHeight;
                LeftControlPanel.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(LeftControlPanel, 0);
                Grid.SetColumn(LeftControlPanel, 0);
                Grid.SetRowSpan(LeftControlPanel, 1);
                Grid.SetColumnSpan(LeftControlPanel, 1);

                LeftControlPanel.ColumnDefinitions.Clear();
                LeftControlPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                LeftControlPanel.Margin = new Thickness(10, PortraitTopVerticalMargin, 10, 0);
                LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(topHeight, GridUnitType.Pixel) });
                LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LeftControlPanel.RowDefinitions.Clear();
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                CoverContainer.VerticalAlignment = VerticalAlignment.Center;
                CoverContainer.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(CoverContainer, 0);
                Grid.SetRowSpan(CoverContainer, 3);
                Grid.SetColumn(CoverContainer, 0);
                Grid.SetColumnSpan(CoverContainer, 1);

                AnimatedTextBlock.Margin = new Thickness(16, 0, 16, 0);
                Grid.SetRow(AnimatedTextBlock, 1);
                Grid.SetColumn(AnimatedTextBlock, 1);
                Grid.SetRowSpan(AnimatedTextBlock, 1);
                Grid.SetColumnSpan(AnimatedTextBlock, 1);
            }
            else
            {
                PlayingDetail.RowDefinitions.Clear();
                PlayingDetail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayingDetail.ColumnDefinitions.Clear();
                PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                PlayingDetail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // 还原 Z-order
                LyricsRegionHost.ClearValue(Canvas.ZIndexProperty);
                LeftControlPanel.ClearValue(Canvas.ZIndexProperty);

                LeftControlPanel.ClearValue(FrameworkElement.HeightProperty);
                LeftControlPanel.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                LeftControlPanel.ClearValue(FrameworkElement.MarginProperty);
                LeftControlPanel.Background = null;

                Grid.SetRow(LeftControlPanel, 0);
                Grid.SetColumn(LeftControlPanel, 0);
                Grid.SetRowSpan(LeftControlPanel, 1);
                Grid.SetColumnSpan(LeftControlPanel, 1);

                LeftControlPanel.ColumnDefinitions.Clear();               
                LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
                LeftControlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                LeftControlPanel.RowDefinitions.Clear();
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8, GridUnitType.Star) });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50, GridUnitType.Pixel) });
                LeftControlPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                CoverContainer.ClearValue(FrameworkElement.MarginProperty);
                CoverContainer.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                CoverContainer.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
                Grid.SetRow(CoverContainer, 1);
                Grid.SetRowSpan(CoverContainer, 1);
                Grid.SetColumn(CoverContainer, 1);
                Grid.SetColumnSpan(CoverContainer, 1);
                AnimatedTextBlock.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                AnimatedTextBlock.ClearValue(FrameworkElement.MarginProperty);
                AnimatedTextBlock.Margin = new Thickness(20, 0, 20, 0);
                Grid.SetRow(AnimatedTextBlock, 2);
                Grid.SetColumn(AnimatedTextBlock, 1);
                Grid.SetRowSpan(AnimatedTextBlock, 1);
                Grid.SetColumnSpan(AnimatedTextBlock, 1);

                Grid.SetRow(LyricsRegionHost, 0);
                Grid.SetColumn(LyricsRegionHost, 1);
                Grid.SetRowSpan(LyricsRegionHost, 1);
                Grid.SetColumnSpan(LyricsRegionHost, 1);
                LyricsRegionHost.Margin = new Thickness(0, 40, 0, 40);
            }

            PlayingDetail.UpdateLayout();
            LeftControlPanel.UpdateLayout();
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
            _ = Task.Run(() =>
            {
                var (_, totalMs) = ViewModel.AppViewModel.GetTimeProgressCache();
                long newPosMs = Math.Max(0, Math.Min((long)(ViewModel.AppViewModel.ProgressSlider * 1000), totalMs));
                ViewModel.AppViewModel.IsManualSelect = true;
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(newPosMs);
                ViewModel.AppViewModel.SetTimeProgressCache(newPosMs, totalMs);
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            _ = App.Services.GetRequiredService<MainPage>().EqualizerDialog.ShowThemedAsync(this.XamlRoot);
        }

        private void CurrentPlayListButtonPlayingDetail_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = true;
            UpdateCurrentPlayList();
        }
        private void UpdateCurrentPlayList()
        {
            if (ViewModel.AppViewModel.CurrentPlayingList is not null)
            {
                if (ViewModel.AppViewModel.CurrentPlayingMusic is not null)
                {
                    var selectedMusic = ViewModel.AppViewModel.CurrentPlayingList.AsValueEnumerable().FirstOrDefault(music =>
                    music.Id == ViewModel.AppViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic is not null)
                    {
                        _ = Task.Delay(100).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                CurrentPlayListViewPlayingDetail.SelectedItem = selectedMusic;
                                CurrentPlayListViewPlayingDetail.ScrollIntoView(selectedMusic);
                            });
                        });
                    }
                }
            }
        }
        private void CurrentPlayListViewPlayingDetail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListViewPlayingDetail.SelectedItem as Music;
            if (selectedMusic is not null)
            {
                _ = App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music: selectedMusic, IsChangeList: false);
            }
        }

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void CurrentPlayListTeachingTipPlayingDetailCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = false;
        }

        private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsWin2dAnimatedText) return;
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = true;
            }
        }

        private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsWin2dAnimatedText) return;
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsWin2dAnimatedText) return;
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
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
            NowPlaying?.PauseRendering();
            LyricsView?.PauseRendering();
        }

        public void ResumeCanvasRendering()
        {
            NowPlaying?.ResumeRendering();
            LyricsView?.ResumeRendering();
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                App.MainWindow?.SizeChanged -= MainWindow_SizeChanged;
                if (App.MainWindow?.AppWindow is { } appWindow)
                    appWindow.Changed -= AppWindow_Changed;
                if (ViewModel?.AppViewModel is { } appvm)
                    appvm.PropertyChanged -= AppViewModel_PropertyChanged;
                LyricsView?.LyricInteracted -= LyricsView_LyricInteracted;
                LyricsView?.ExceptionInteracted -= LyricsView_ExceptionInteracted;
                LyricsView?.ShutdownLyricsCanvas();
                AlbumArtControl?.Dispose();
                NowPlaying?.ExceptionOccurred -= BackGround_ExceptionOccurred;
                NowPlaying?.Dispose();
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
            Task.Run(() =>
            {
                AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.LyricLine? lyricLine = ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.StartMs >= e.TotalMilliseconds);
                ViewModel.AppViewModel.IsManualSelect = true;
                if (lyricLine is not null)
                {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(lyricLine);
                    ViewModel.UpdateLyricsToUI(index);
                }
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime((long)e.TotalMilliseconds);
                ViewModel.AppViewModel.SetTimeProgressCacheCurMs((long)e.TotalMilliseconds);
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

        private void NowPlaying_LyricLineClicked(object? sender, TimeSpan e)
        {
            LyricsView_LyricInteracted(sender, e);
        }

        private void LyricsRegionHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLyricsRegion();
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
