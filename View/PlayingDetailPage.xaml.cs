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
        //private ToolTip _progressToolTip = new();
        public PlayingDetailPage(PlayingDetailViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            Loaded += PlayingDetailPage_Loaded;
            _logger = App.GetLogger<PlayingDetailPage>();
        }

        private void PlayingDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
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
            ChangeControlsFontSize();
            Loaded -= PlayingDetailPage_Loaded;
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidVisibilityChange)
            {
                bool visible = sender.IsVisible;
                BackGround?.SetWindowPaused(!visible);
                LyricsView?.SetWindowPaused(!visible);
            }
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            ChangeControlsFontSize();
        }

        private void ChangeControlsFontSize()
        {
            var windowSize = App.MainWindow.AppWindow.Size;
            _dpiScale = WindowSizeHelper.GetScaleFactor(AppData.HWnd);
            double width = windowSize.Width / _dpiScale;
            double height = windowSize.Height / _dpiScale;
            double effectiveSize = width * height;
            var lyrics = effectiveSize switch
            {
                <= 1280 * 720 => 48,
                <= 1600 * 900 => 60,
                <= 1920 * 1080 => 72,
                <= 2560 * 1440 => 84,
                <= 2880 * 1920 => 96,
                _ => 120
            };

            var (title, artist, firstSize, secondSize, shapeSize, info, margin) = width switch
            {
                < 1280 => (26, 22, 22, 16, 28, 12, new Thickness(2, 0, 2, 0)),
                < 1920 => (32, 28, 28, 22, 40, 16, new Thickness(5, 0, 5, 0)),
                < 2560 => (36, 32, 36, 26, 60, 18, new Thickness(10, 0, 10, 0)),
                < 2880 => (38, 34, 40, 28, 66, 20, new Thickness(12, 0, 12, 0)),
                _ => (42, 38, 46, 32, 72, 22, new Thickness(15, 0, 15, 0))
            };

            // 4. 应用变更
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                ViewModel.TitleFontSize = title;
                ViewModel.ArtistAlbumFontSize = artist;
                ViewModel.InfoFontSize = info;
                ViewModel.AppViewModel.LyricsFontSize = lyrics;
                ViewModel.FirstControlSize = firstSize;
                ViewModel.SecondControlSize = secondSize;
                ViewModel.FirstShapeSize = shapeSize;
                ViewModel.ControlMargin = margin;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = title;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = artist;
            });
        }

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            App.Services.GetRequiredService<MainPage>().NavigatebackToMusicBrowsePage();
        }

        private void TopControl_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.TopControlsOpacity = 1.0f;
        }

        private void TopControl_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.AppViewModel.IsPlayDetailButtonVisible)
            {
                ViewModel.AppViewModel.TopControlsOpacity = 0.0f;
            }
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
                //thumb.DragDelta += (s, e) =>
                //{
                //    _progressToolTip?.Content = ViewModel.AppViewModel.ProgressSliderThumbTipText;
                //};

                //ToolTipService.SetToolTip(thumb, _progressToolTip);
                //_progressToolTip.Opened += (s, e) =>
                //    _progressToolTip.Content = ViewModel.AppViewModel.ProgressSliderThumbTipText;
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
            App.Services.GetRequiredService<MainPage>().EqualizerDialog.RequestedTheme = AppSettings.ElementTheme;
            App.Services.GetRequiredService<MainPage>().EqualizerDialog.XamlRoot = this.XamlRoot;
            _ = App.Services.GetRequiredService<MainPage>().EqualizerDialog.ShowAsync();
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
                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music: selectedMusic, IsChangeList: false).Wait();
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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void PauseCanvasRendering()
        {
            BackGround?.PauseRendering();
            LyricsView?.PauseRendering();
        }

        public void ResumeCanvasRendering()
        {
            BackGround?.ResumeRendering();
            LyricsView?.ResumeRendering();
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                App.MainWindow.SizeChanged -= MainWindow_SizeChanged;
                App.MainWindow.AppWindow.Changed -= AppWindow_Changed;
                LyricsView?.LyricInteracted -= LyricsView_LyricInteracted;
                LyricsView?.ExceptionInteracted -= LyricsView_ExceptionInteracted;
                LyricsView?.ShutdownLyricsCanvas();
                AlbumArtControl?.Dispose();
                BackGround?.ExceptionOccurred -= BackGround_ExceptionOccurred;
                BackGround?.Dispose();
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
                LyricLine? lyricLine = ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.StartMs >= e.TotalMilliseconds);
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

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            App.Services.GetRequiredService<MainPage>().SettingsDialog.RequestedTheme = AppSettings.ElementTheme;
            App.Services.GetRequiredService<MainPage>().SettingsDialog.XamlRoot = this.XamlRoot;
            _ = App.Services.GetRequiredService<MainPage>().SettingsDialog.ShowAsync();
        }
    }
}
