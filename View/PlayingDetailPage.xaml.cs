using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;
using WinUIMusicPlayer.Controls;
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
    public sealed partial class PlayingDetailPage : Page,IDisposable
    {
        public PlayingDetailViewModel ViewModel { get; }

        public PlayingDetailPage(PlayingDetailViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            Loaded += PlayingDetailPage_Loaded;
        }

        private void PlayingDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsWin2dAnimatedText)
            {
                // ====================== 核心 switch 完整代码 ======================
                switch (ViewModel.AppViewModel.Win2dTextEffectType)
                {
                    case AnimatedTextEffect.TextBlurEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextBlurEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextBlurEffect();
                        break;

                    case AnimatedTextEffect.TextDefaultEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextDefaultEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextDefaultEffect();
                        break;

                    case AnimatedTextEffect.TextElasticEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextElasticEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextElasticEffect();
                        break;

                    case AnimatedTextEffect.TextFadeEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextFadeEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextFadeEffect();
                        break;

                    case AnimatedTextEffect.TextMotionBlurEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextMotionBlurEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextMotionBlurEffect();
                        break;

                    case AnimatedTextEffect.TextPivotEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextPivotEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextPivotEffect();
                        break;

                    case AnimatedTextEffect.TextWipeEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextWipeEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextWipeEffect();
                        break;

                    case AnimatedTextEffect.TextZoomEffect:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextZoomEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextZoomEffect();
                        break;

                    default:
                        AnimatedPlayingDetailTitleTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextDefaultEffect();
                        AnimatedPlayingDetailAlbumArtistTextBlock?.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextDefaultEffect();
                        break;
                }
            }
            App.MainWindow.SizeChanged += MainWindow_SizeChanged;
            ChangeControlsFontSize();
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            ChangeControlsFontSize();
        }

        private void ChangeControlsFontSize() {
            var width =  App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale;
            if (width <= 1440)
            {
                ViewModel.TitleFontSize = 24;
                ViewModel.ArtistAlbumFontSize = 22;
                ViewModel.InfoFontSize = 12;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 24;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 22;
            }
            else if (width <= 1680)
            {
                ViewModel.TitleFontSize = 26;
                ViewModel.ArtistAlbumFontSize = 24;
                ViewModel.InfoFontSize = 13;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 26;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 24;
            }
            else if (width <= 1920)
            {
                ViewModel.TitleFontSize = 28;
                ViewModel.ArtistAlbumFontSize = 26;
                ViewModel.InfoFontSize = 14;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 28;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 26;
            }
            else if (width <= 2160)
            {
                ViewModel.TitleFontSize = 30;
                ViewModel.ArtistAlbumFontSize = 28;
                ViewModel.InfoFontSize = 15;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 30;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 28;
            }
            else if (width <= 2560)
            {
                ViewModel.TitleFontSize = 32;
                ViewModel.ArtistAlbumFontSize = 30;
                ViewModel.InfoFontSize = 16;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 32;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 30;
            }
            else
            {
                ViewModel.TitleFontSize = 36;
                ViewModel.ArtistAlbumFontSize = 34;
                ViewModel.InfoFontSize = 18;
                AnimatedPlayingDetailTitleTextBlock?.FontSize = 36;
                AnimatedPlayingDetailAlbumArtistTextBlock?.FontSize = 34;
            }
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

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                Task.Run(() =>
                {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= lyricLine.Time));
                    ViewModel.AppViewModel.IsManualSelect = true;
                    ViewModel.UpdateLyricsToUI(index);
                    App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(lyricLine.Time);
                    ViewModel.AppViewModel.IsManualSelect = false;
                });
            }
        }

        private void LyricsLineGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                          .OfType<BlurEffectControl>()
                          .FirstOrDefault();
                blurControl?.GetBlurEffectManager()?.StartBlurReverseAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                if (AppSettings.LyricsBlurAmount < 1)
                {
                    if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out var resourceValue))
                    {
                        var secondaryBrush = resourceValue as SolidColorBrush;
                        grid?.Background = secondaryBrush ?? new(Color.FromArgb(25, 255, 255, 255));
                    }
                }
            }
        }

        private void LyricsLineGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var blurControl = grid?.Children.AsValueEnumerable()
                        .OfType<BlurEffectControl>()
                        .FirstOrDefault();
                blurControl?.GetBlurEffectManager()?.StartBlurAnimation(AppSettings.LyricsBlurAmount, TimeSpan.FromMilliseconds(350));
                if (AppSettings.LyricsBlurAmount < 1)
                {
                    grid?.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
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
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = true;
        }

        private async void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.AppViewModel.IsUserDraggingProgressSlider = false;
            double newPosition = Math.Max(0, Math.Min(ViewModel.AppViewModel.ProgressSlider, await App.Services.GetRequiredService<BassPlayerCommandService>().GetTotalPosition()));
            _ = Task.Run(() =>
            {
                ViewModel.AppViewModel.IsManualSelect = true;
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
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
                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music: selectedMusic, IsChangeList: false);
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
            if(ViewModel.AppViewModel.IsWin2dAnimatedText) return;
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

        private void Dispose(bool dispose)
        {
            if (dispose) {
                App.MainWindow.SizeChanged -= MainWindow_SizeChanged;
                AlbumArtControl?.Dispose();
                BackGround?.Dispose();
            }
        }

        private void BackGround_ThemeResolved(object sender, bool isDark)
        {
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
    }
}
