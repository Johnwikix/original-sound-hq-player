using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects;
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
using WinUIMusicPlayer.Controls.Lyrics;
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
            ChangeControlsFontSize();
        }

        private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
        {
            ChangeControlsFontSize();
        }

        private void ChangeControlsFontSize()
        {
            // 1. 获取缩放后的逻辑宽高
            var windowSize = App.MainWindow.AppWindow.Size;
            double width = windowSize.Width / AppData.AppDpiScale;
            double height = windowSize.Height / AppData.AppDpiScale;

            // 2. 取宽高中的较小值（或加权平均值）作为判定基准
            // 这样可以防止窗口高度过小时，字体依然按宽度显示得巨大
            double effectiveSize = Math.Min(width, height * 1.77); // 1.77 是 16:9 的比例参考，可根据需求调整

            // 3. 这里的阈值可以根据 effectiveSize 重新微调
            var (title, artist, info, lyrics) = effectiveSize switch
            {
                <= 720 => (22, 20, 11, 28), // 针对小窗口或窄高窗口的基准
                <= 1080 => (24, 22, 12, 32),
                <= 1440 => (26, 24, 13, 36),
                <= 1920 => (28, 26, 14, 40),
                <= 2160 => (32, 30, 16, 48),
                _ => (36, 34, 18, 52)
            };

            // 4. 应用变更
            App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                ViewModel.TitleFontSize = title;
                ViewModel.ArtistAlbumFontSize = artist;
                ViewModel.InfoFontSize = info;
                ViewModel.AppViewModel.LyricsFontSize = lyrics;

                // 建议：UI 元素赋值也放入 DispatcherQueue 以确保线程安全
                if (AnimatedPlayingDetailTitleTextBlock != null)
                    AnimatedPlayingDetailTitleTextBlock.FontSize = title;
                if (AnimatedPlayingDetailAlbumArtistTextBlock != null)
                    AnimatedPlayingDetailAlbumArtistTextBlock.FontSize = artist;
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

        private void LyricsControl_LyricInteracted(object sender, TimeSpan e)
        {
            Task.Run(() =>
            {
                LyricLine? lyricLine = ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= e);
                ViewModel.AppViewModel.IsManualSelect = true;
                if (lyricLine is not null) {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(lyricLine);
                    ViewModel.UpdateLyricsToUI(index);
                } 
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(e);
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }

        private void LyricsView_LyricInteracted(object sender, TimeSpan e)
        {
            Task.Run(() =>
            {
                LyricLine? lyricLine = ViewModel.AppViewModel.UILyrics.AsValueEnumerable().FirstOrDefault(line => line.Time >= e);
                ViewModel.AppViewModel.IsManualSelect = true;
                if (lyricLine is not null)
                {
                    int index = ViewModel.AppViewModel.UILyrics.IndexOf(lyricLine);
                    ViewModel.UpdateLyricsToUI(index);
                }
                App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(e);
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }
    }
}
