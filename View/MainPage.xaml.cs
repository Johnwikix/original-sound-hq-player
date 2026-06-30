using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Pages;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainViewModel ViewModel { get; }
        public EqualizerDialog EqualizerDialog { get; set; }
        public SettingsDialog SettingsDialog { get; set; }
        public AddPlayListDialog AddPlayListDialog { get; set; }
        private readonly INavigationService _playingNavigation;
        private bool _isPageTransitioning = false;

        public bool IsPlayingDetailVisible => PlayingFrame.Visibility == Visibility.Visible;
        //private ToolTip _progressToolTip = new();
        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            MainFrame.Navigated += MainFrame_Navigated;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _playingNavigation = navigationServiceFactory.CreateNavigationService(PlayingFrame);
            _playingNavigation.RegisterPage<PlayingDetailPage>();             
            ViewModel.MusicBrowseVM.SetMainPage(this);
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            NavigationViewControl.IsPaneOpen = false;
            NavigateToDefaultPage();
            InitiaizeEqualizerDialog();
            SetSettingsDialog();
            AddPlayListDialog ??= new AddPlayListDialog(ViewModel.AppViewModel);
            NavigationViewControl.Visibility = Visibility.Visible;
            Loaded -= MainPage_Loaded;
        }

        private void MainFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }

        private void NavigateTo(Type pageType, object? parameter = null, NavigationTransitionInfo? navigationTransitionInfo = null)
        {            
            MainFrame.Navigate(pageType, parameter, navigationTransitionInfo);
            MainFrame.BackStack.Clear();
        }

        private void InitiaizeEqualizerDialog()
        {
            if (EqualizerDialog is null)
            {
                EqualizerDialog = new EqualizerDialog();
                EqualizerDialog.EqualizerGainChanged += (s, frequency) =>
                {
                    int feq = ToolUtils.FrequencyIndexMap[frequency];
                    ViewModel.PlayerCommandService.SetEqualizerGain((byte)feq, (float)AppSettings.Equalizer[frequency]);
                };
                EqualizerDialog.ClearEqualizer += (s, e) =>
                {
                    ViewModel.PlayerCommandService.UpdateSettings();
                    if (AppSettings.IsEqualizerEnabled)
                    {
                        ViewModel.PlayerCommandService.ToggleEqualizer();
                        ViewModel.PlayerCommandService.SetEqualizer();
                    }
                    else
                    {
                        ViewModel.PlayerCommandService.ClearEqualizer();
                    }
                };
            }
        }

        private void SetSettingsDialog()
        {
            SettingsDialog ??= new SettingsDialog(ViewModel.AppViewModel);
        }

        private void NavigateToDefaultPage()
        {

            foreach (var item in NavigationViewControl.MenuItems)
            {
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == ViewModel.AppViewModel.DefaultEntryComboBoxTag)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (ViewModel.AppViewModel.DefaultEntryComboBoxTag)
            {
                case "AddFolder":
                    NavigateTo(typeof(AddFolderPage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "MusicBrowse":
                    NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                    break;
                case "PlayLists":
                    NavigateTo(typeof(PlayListPage), null, new EntranceNavigationTransitionInfo());
                    break;
                default:
                    NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                NavigationViewControl.Visibility = Visibility.Visible;
                _playingNavigation.Dismiss(300);
            }
            if (MainFrame.Content is not SettingsPage) {
                NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
                NavigateTo(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
            }            
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            Type? targetType = null;

            if (args.IsSettingsInvoked)
            {
                targetType = typeof(SettingsPage);
            }
            else
            {
                targetType = args.InvokedItemContainer.Tag.ToString() switch
                {
                    "AddFolder" => typeof(AddFolderPage),
                    "MusicBrowse" => typeof(MusicBrowsePage),
                    "PlayLists" => typeof(PlayListPage),
                    _ => null
                };
            }

            if (targetType is not null && MainFrame.Content?.GetType() != targetType)
            {
                NavigateTo(targetType, null, new EntranceNavigationTransitionInfo());
            }
        }

        public void NavigateToMusicBrowsePage()
        {
            if (MainFrame.Content is not MusicBrowsePage)
            {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                NavigateTo(typeof(MusicBrowsePage), null, new EntranceNavigationTransitionInfo());
            }
        }

        public void NavigateToPlayingDetailPage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }                
                _playingNavigation.Show(typeof(PlayingDetailPage), 300, onCompleted: OnOneCompleted);
                NavigationViewControl.Visibility = Visibility.Collapsed;
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        public void NavigatebackToMusicBrowsePage()
        {
            if (_isPageTransitioning) return;
            _isPageTransitioning = true;

            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                var pendingCount = 1;
                void OnOneCompleted()
                {
                    if (Interlocked.Decrement(ref pendingCount) == 0)
                        _isPageTransitioning = false;
                }                
                _playingNavigation.Dismiss(300, onCompleted: OnOneCompleted);
                NavigationViewControl.Visibility = Visibility.Visible;
            }
            else
            {
                _isPageTransitioning = false;
            }
        }

        public void HandleBackNavigation()
        {
            if (MainFrame.Content is PlayListPage playListPage && playListPage.ViewModel.IsInDetailMode)
            {
                playListPage.CollapseDetail();
                return;
            }
            App.Services.GetRequiredService<MusicBrowseViewModel>().BackButton();
        }

        private void NavigationViewControl_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            HandleBackNavigation();
        }

        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
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

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverProgressBar = false;
        }

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.AppViewModel.IsMouseOverVolumeSlider = false;
        }

        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (ViewModel.AppViewModel.IsMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
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
                ViewModel.PlayerCommandService.ChangeWaveChannelTime(newPosMs);
                ViewModel.AppViewModel.SetTimeProgressCache(newPosMs, totalMs);
                ViewModel.AppViewModel.IsManualSelect = false;
            });
        }

        private void AuthorTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                ViewModel.MusicBrowseVM.SelectBarArtist(artist);
            }
        }

        private void AlbumTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                ViewModel.MusicBrowseVM.SelectBarAlbum(albumName);
            }
        }

        private void CurrentPlayListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListView.SelectedItem as Music;
            if (selectedMusic is not null)
            {
                _ = ViewModel.MusicBrowseVM.PlayMusic(music: selectedMusic, IsChangeList: false);
            }
        }

        private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = true;
            }
        }

        private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void CurrentPlayListButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = true;
            UpdateCurrentPlayList();
        }

        private void CurrentPlayListTeachingTipCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = false;
        }

        public void UpdateCurrentPlayList()
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
                                CurrentPlayListView.SelectedItem = selectedMusic;
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
                            });
                        });
                    }
                }
            }
        }
    }
}
