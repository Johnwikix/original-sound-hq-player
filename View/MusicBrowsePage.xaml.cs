using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        public MainWindow mainWindow;
        private bool isMouseOverVolumeSlider = false;
        private DispatcherTimer typingTimer;  
        private NotificationService notificationService;
        public EventHandler refreshSong;
        public EventHandler refreshPage;
        public EventHandler<PlayList> addPlayListEvent;
        private readonly INavigationService _navigationService;
        private EqualizerDialog equalizerDialog;
        private bool isSearching = false;
        private string lastSearchText = string.Empty;
        private AcrylicBrush acrylicBrush = new AcrylicBrush { TintOpacity = 0.5 };

        public MusicBrowseViewModel ViewModel { get; }
        public MusicBrowsePage(MusicPlaybackService musicPlaybackService,
            NotificationService notificationService, 
            MusicBrowseViewModel viewModel
            )
        {           
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetMusicService(musicPlaybackService);
            ViewModel.SetMusicBrowsePage(this);
            DataContext = this;
            ViewModel.IsInitialized = false;
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.ContentFrame = ContentFrame;
            _navigationService.RegisterPage<FavouritePlayListPage>();
            _navigationService.RegisterPage<SongCollectionPage>();
            _navigationService.RegisterPage<AlbumPage>();
            _navigationService.RegisterPage<ArtistPage>();
            _navigationService.RegisterPage<FolderBrowsePage>();
            _navigationService.RegisterPage<PlayListPage>();
            _navigationService.RegisterPage<PlayListSongPage>();
            _navigationService.RegisterPage<SongListPage>();
            ProgressSlider.Loaded += ProgressSlider_Loaded;            
            //this.KeyDown += MusicBrowsePage_KeyDown;
            this.Focus(FocusState.Programmatic);
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.updateMusicList += MainWindow_updateMusicList;
                mainWindow.updateSelectSection += MainWindow_updateSelectSection;
            }
            equalizerDialog = new EqualizerDialog();
            equalizerDialog.EqualizerGainChanged += (s, frequency) =>
            {
                float feq= FrequencyMap[frequency];
                musicPlaybackService.SetEqualizerGain(feq, (float)AppSettings.equalizer[frequency]);
            };
            equalizerDialog.clearEqualizer += (s, e) =>
            {
                Task.Run(() =>
                {
                    if (!AppSettings.IsEqualizerEnabled)
                    {
                        musicPlaybackService.ClearEqualizer();
                    }
                    else
                    {
                        musicPlaybackService.SetEqualizer();
                        musicPlaybackService.ToggleEqualizer();
                    }
                });             
            };
            this.notificationService = notificationService;
            InitializeTimer();
            SetAcrylicBrushBackground();            
            ViewModel.OnFileChanged(null, null);
            InitializeSortComboBox();
            ViewModel.IsInitialized = true;
            //TODO 波形可视化
         
        }

        public void NavigatePage(System.Type currentPage, NavigationTransitionInfo navigationTransitionInfo,int animeTime) {
            _navigationService.Navigate(currentPage, this,navigationTransitionInfo , animeTime, true);
        }

        private void MainWindow_updateSelectSection(object? sender, EventArgs e)
        {
            SelectBarItem(AppSettings.DefualtPlayList);
        }

        private void SetAcrylicBrushBackground() {

            ChangeAcrylicBrushBackground();
            AcrylicBrushBackground.Background = acrylicBrush;
            ChangeAcrylicBrushBackgroundOpacity();
        }

        public void ChangeAcrylicBrushBackground()
        {

            if (((FrameworkElement)App.MainWindow!.Content).ActualTheme == ElementTheme.Dark)
            {
                acrylicBrush.TintColor = Colors.Black;
            }
            else
            {
                acrylicBrush.TintColor = Colors.White;
            }
        }

        public void ChangeAcrylicBrushBackgroundOpacity()
        {
            ViewModel.IsAcrylicBrushOpacity = ViewModel.MusicDetailCover != null && ViewModel.IsInPlayingDetailMode && AppSettings.IsBackgroundCoverEnabled ? true : false;
            AcrylicBrushBackground.Background.Opacity = ViewModel.IsAcrylicBrushOpacity ? 1.0 : 0;
        }

        public async void MainWindow_updateMusicList(object? sender, EventArgs e)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                refreshPage?.Invoke(this, EventArgs.Empty);
                refreshSong?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SelectBarItem(string name)
        {
            foreach (var item in selectPage.Items)
            {
                if (item is SelectorBarItem selectorBarItem && selectorBarItem.Tag.ToString() == name)
                {
                    ViewModel.SelectedPage = selectorBarItem;
                    break;
                }
            }
        }

        public async Task ClosePage() {
            await ViewModel._musicPlaybackService.DisposeAudio();
            if (mainWindow != null)
            {
                mainWindow.updateSelectSection -= MainWindow_updateSelectSection;
                mainWindow.updateMusicList -= MainWindow_updateMusicList;             
            }
        }

        private void InitializeTimer()
        {
            typingTimer = new DispatcherTimer();
            typingTimer.Interval = TimeSpan.FromMilliseconds(300);
            typingTimer.Tick += TypingTimer_Tick;
        }
        public void ShowTransmission()
        {
            ViewModel.ProcessRingVisibility = Visibility.Visible;
        }

        public void HideTransmission()
        {
            ViewModel.ProcessRingVisibility = Visibility.Collapsed;
        }

        //public void ClearUsbDeviceMusicList() {
        //    clearUsbDeviceMusicList?.Invoke(this, EventArgs.Empty);
        //}

        private async void UsbDeviceCombox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppData.usbStorageDevice = null;
            AppData.musicOnUsbDevice.Clear();
            ToolUtils.ClearAllUsbStatus();
            if (UsbDeviceCombox.SelectedItem is UsbStorageDevice usbStorageDevice)
            {
                ViewModel.UsbDeviceComboxSelectionChanged(usbStorageDevice);
            }
        }

        public void DisableBackButton()
        {
            if (mainWindow != null)
            {
                if (ViewModel.currentPage == typeof(SongCollectionPage) || ViewModel.currentPage == typeof(PlayListSongPage))
                {
                    mainWindow.DisableEnableBackButton(true);
                }
                else
                {
                    mainWindow.DisableEnableBackButton(false);
                }
            }            
        }        

        public void SelectBarAlbum(string Album)
        {
            ViewModel.PageType = "album";
            ViewModel.paramName = Album;
            ViewModel.CurrentAlbum = AppData.allSongs.Where(m => m.Album == Album).OrderBy(music => music.TrackNumber).FirstOrDefault();
            ViewModel.CurrentAlbum.Album = Album;
            ViewModel.currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    _navigationService.Navigate(ViewModel.currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("album");
                }
            }
        }      

        public void SelectBarArtist(string artist)
        {
            ViewModel.PageType = "artist";
            ViewModel.paramName = artist;
            ViewModel.CurrentArtist = AppData.allSongs.FirstOrDefault(m => m.Author == artist);
            ViewModel.CurrentArtist.Author = artist;
            ViewModel.currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    _navigationService.Navigate(ViewModel.currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("artist");
                }
            }
        }

        public async Task AddToFavourite(Music music)
        {
            music.IsFavorite = !music.IsFavorite;
            await MusicDatabaseService.AddToFavourite(music, ViewModel.CurrentPlayingMusic);
            if (ViewModel.CurrentPlayingMusic != null)
            {
                if (ViewModel.CurrentPlayingMusic.Id == music.Id)
                {
                    ViewModel.CurrentPlayingMusic.IsFavorite = music.IsFavorite;
                }
            }
        }


        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            typingTimer.Stop();
            typingTimer.Start();
        }

        private async void TypingTimer_Tick(object sender, object e)
        {
            typingTimer.Stop();

            try {
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 防止重入
                    if (isSearching)
                    {
                        return;
                    }

                    var currentText = SearchTextBox.Text;

                    // 防止重复搜索
                    if (currentText == lastSearchText)
                    {
                        return;
                    }

                    isSearching = true;
                    lastSearchText = currentText;

                    try
                    {
                        AppData.searchText = currentText;
                        if (ContentFrame?.Content != null)
                        {
                            refreshPage?.Invoke(this, EventArgs.Empty);
                            refreshSong?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"搜索执行失败: {ex.Message}");
                    }
                    finally
                    {
                        isSearching = false;
                    }
                });
            } catch (Exception ex) {
                Debug.WriteLine($"搜索处理异常: {ex.Message}");
            }
        }

        public void BackButton()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                switch (ViewModel.PageType)
                {
                    case "album":
                        ViewModel.currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "artist":
                        ViewModel.currentPage = typeof(ArtistPage);
                        _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "folder":
                        ViewModel.currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    default:
                        break;
                }
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                ViewModel.currentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            DisableBackButton();
        }

        private async void AddPlayList_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog contentDialog = new ContentDialog
            {
                Title = ToolUtils.GetString("FlyoutAddToPlaylist"),
                Content = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = ToolUtils.GetString("EnterPlaylistName") },
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.elementTheme;
            ContentDialogResult result = await contentDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                string playlistName = textBox.Text;
                if (!string.IsNullOrEmpty(playlistName))
                {
                    PlayList newPlaylist = new PlayList { Name = playlistName };
                    await MusicDatabaseService.InsertPlayList(newPlaylist);
                    addPlayListEvent?.Invoke(this, newPlaylist);
                }
            }
        }

        private async void CurrentPlayListButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = true;
            UpdateCurrentPlayList();
        }

        private void UpdateCurrentPlayList()
        {
            if (ViewModel.CurrentPlayingList != null)
            {
                if (ViewModel.CurrentPlayingMusic != null)
                {
                    var selectedMusic = ViewModel.CurrentPlayingList.FirstOrDefault(music =>
                    music.Id == ViewModel.CurrentPlayingMusic.Id);

                    if (selectedMusic != null)
                    {                        
                        _ = Task.Delay(100).ContinueWith(_ =>
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                CurrentPlayListView.SelectedItem = selectedMusic;
                                CurrentPlayListViewPlayingDetail.SelectedItem = selectedMusic;
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
                                CurrentPlayListViewPlayingDetail.ScrollIntoView(selectedMusic);
                            });
                        }); 
                    }
                }                
            }
        }

        private void CurrentPlayListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListView.SelectedItem as Music;
            if (selectedMusic != null)
            {
                PlayMusic(selectedMusic);
            }
        }       

        private void UpdateViewList(Music music)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var songListPage = ContentFrame.Content as SongListPage;
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                var FavouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                var playListSongPage = ContentFrame.Content as PlayListSongPage;
                if (songListPage != null)
                {
                    songListPage.UpdateMusicListView();
                }
                if (songCollectionPage != null)
                {
                    songCollectionPage.UpdateMusicListView();
                }
                if (FavouritePlayListPage != null)
                {
                    FavouritePlayListPage.UpdateMusicListView();
                }
                if (playListSongPage != null)
                {
                    playListSongPage.UpdateMusicListView();
                }
            });
        }

        public void PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            try
            {
                //_forceDrawTimer.Start();                
                ViewModel.CurrentPlayingMusic = music;
                ViewModel.LoadLyricsToUI();
                ViewModel.UpdatePlayBar(ViewModel.CurrentPlayingMusic);
                UpdateViewList(music);
                UpdateCurrentPlayList();
                _ = Task.Run(async () =>
                {
                    ViewModel._musicPlaybackService.PlayMusic(music, currentPos, isSettingChanged);
                });
                
                
            }
            catch (Exception ex)
            {
                notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
            }
        }

        private void InitializeSortComboBox()
        {
            var matchingItem = SortByComboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == AppData.sortOrder);

            SortByComboBox.SelectedItem = matchingItem ?? SortByComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault();
        }

        private void SortByComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppData.sortOrder = selectedItem.Tag.ToString();
                if (ContentFrame != null && ContentFrame.Content != null)
                {
                    if (ContentFrame.Content is SongCollectionPage)
                    {
                        var page = ContentFrame.Content as SongCollectionPage;
                        page.SortMusicList(AppData.sortOrder, ViewModel.PageType);
                    }
                    if (ContentFrame.Content is SongListPage)
                    {
                        var page = ContentFrame.Content as SongListPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FavouritePlayListPage)
                    {
                        var page = ContentFrame.Content as FavouritePlayListPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is AlbumPage)
                    {
                        var page = ContentFrame.Content as AlbumPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is PlayListSongPage)
                    {
                        var page = ContentFrame.Content as PlayListSongPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                }
            }
        }
        
        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = false;
        }

        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.IsMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.IsMouseOverProgressBar = false;
        }
        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSlider);
            if (thumb != null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }
        private void ProgressSliderPlayingDetail_Loaded(object sender, RoutedEventArgs e)
        {
            var thumb = FindVisualChild<Thumb>(ProgressSliderPlayingDetail);
            if (thumb != null)
            {
                thumb.DragStarted += Thumb_DragStarted;
                thumb.DragCompleted += Thumb_DragCompleted;
            }
        }



        private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (isMouseOverVolumeSlider)
            {
                var delta = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
                if (delta > 0)
                {
                    ViewModel.AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    ViewModel.AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                SelectBarArtist(artist);
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                SelectBarAlbum(albumName);
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            ViewModel.IsUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ViewModel.IsUserDraggingProgressSlider = false;
            if (ViewModel._musicPlaybackService.multiTypeAudioReader != null)
            {
                double newPosition = Math.Max(0, Math.Min(ViewModel.ProgressSlider, ViewModel._musicPlaybackService.multiTypeAudioReader.TotalTime.TotalSeconds));
                _=Task.Run(() =>
                {
                    ViewModel._musicPlaybackService.isManualSelect = true;
                    ViewModel._musicPlaybackService.ChangeWaveChannelTime(TimeSpan.FromSeconds(newPosition));
                    ViewModel._musicPlaybackService.isManualSelect = false;
                });                
            }
        }

        private async void AlbumCoverImage_Click(object sender, RoutedEventArgs e)
        {
            await ShowPlayingDetail();
        }

        public async Task ShowPlayingDetail() {
            if (!ViewModel.IsInPlayingDetailMode) {
                ViewModel.IsInPlayingDetailMode = true;
                App.MainWindow.IsPlayingDetail = true;
                App.MainWindow.NavigationViewCollapsed();
                ChangeAcrylicBrushBackgroundOpacity();
                mainWindow?.AppTitleBarVisibility(false);             
                if (ViewModel.CurrentPlayingMusic != null && AlbumCoverImage != null)
                {
                    ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("CoverToDetail", AlbumCoverImageGrid);
                    ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("MusicInfoToDetail", AlbumCoverAuthorTitleModel);
                    ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("ControlBarToDetail", BottomControlBar);
                    ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("MusicInfoToDetailLyrics", VolumeDown);
                    TopPanel.Visibility = Visibility.Collapsed;
                    ContentFrame.Visibility = Visibility.Collapsed;
                    AlbumCoverAuthorTitleModel.Visibility = Visibility.Collapsed;
                    ViewModel.InfoBarIsOpen = false;
                    PlayingDetail.Visibility = Visibility.Visible;
                    BottomControlBar.Visibility= Visibility.Collapsed;
                    PlayingDetailControlBar.Visibility = Visibility.Visible;
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("ControlBarToDetail").Configuration = new DirectConnectedAnimationConfiguration();
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetailLyrics").Configuration = new DirectConnectedAnimationConfiguration();
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail").TryStart(PlayingDetailAlbumCoverImageGrid);
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetail").TryStart(MusicInfoPanel);
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("ControlBarToDetail").TryStart(PlayingDetailControlBar);
                    ConnectedAnimationService.GetForCurrentView().GetAnimation("MusicInfoToDetailLyrics").TryStart(LyricViewer);
                }
                ProgressSliderPlayingDetail.Loaded += ProgressSliderPlayingDetail_Loaded;
            }            
        }

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsInPlayingDetailMode = false;
            App.MainWindow.IsPlayingDetail = false;
            App.MainWindow.NavigationViewExpanded();
            ChangeAcrylicBrushBackgroundOpacity();
            mainWindow?.AppTitleBarVisibility(true);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToCover", PlayingDetailAlbumCoverImageGrid);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToMusicInfo", MusicInfoPanel);
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToControlBar", PlayingDetailControlBar);
            TopPanel.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Visible;
            PlayingDetail.Visibility = Visibility.Collapsed;
            AlbumCoverAuthorTitleModel.Visibility = Visibility.Visible;
            BottomControlBar.Visibility = Visibility.Visible;
            PlayingDetailControlBar.Visibility = Visibility.Collapsed;
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToMusicInfo").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToControlBar").Configuration = new DirectConnectedAnimationConfiguration();
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover").TryStart(AlbumCoverImageGrid);
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToMusicInfo").TryStart(AlbumCoverAuthorTitleModel);
            ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToControlBar").TryStart(BottomControlBar);
            ProgressSliderPlayingDetail.Loaded -= ProgressSliderPlayingDetail_Loaded;
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            equalizerDialog.RequestedTheme = AppSettings.elementTheme;
            equalizerDialog.XamlRoot = this.XamlRoot;
            _=equalizerDialog.ShowAsync();           
        }

        private void LyricsTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var textblock = (TextBlock)sender;
            if (textblock.FontSize == 28 || textblock.FontSize == 32 || textblock.FontSize == 34 || textblock.FontSize == 36 || textblock.FontSize == 40)
            {
                var currentScrollPosition = LyricViewer.VerticalOffset;
                var point = new Point(0, currentScrollPosition);

                // 计算出目标位置并滚动
                var targetPosition = textblock.TransformToVisual(LyricViewer).TransformPoint(point);

                LyricViewer.ChangeView(
                    null,
                    (targetPosition.Y - LyricViewer.ActualHeight / 2) + 30,
                    null,
                    disableAnimation: false
                );
            }
        }

        private void LyricsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LyricLine lyricLine)
            {
                Task.Run(() =>
                {
                    int index = ViewModel.UILyrics.IndexOf(ViewModel.UILyrics.FirstOrDefault(line => line.Time >= lyricLine.Time));
                    ViewModel.UpdateLyricsToUI(index);
                    ViewModel._musicPlaybackService.isManualSelect = true;
                    ViewModel._musicPlaybackService.ChangeWaveChannelTime(lyricLine.Time);
                    ViewModel._musicPlaybackService.isManualSelect = false;
                });
            }
        }

        private void LyricsTextBlock_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                textBlock.Opacity = 1.0;
            }
        }

        private void LyricsTextBlock_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.DataContext is LyricLine lyricLine)
            {
                if (!lyricLine.IsCurrent)
                {
                    textBlock.Opacity = 0.7;
                }
                else
                {
                    textBlock.Opacity = 1.0;
                }
            }
        }

        private void CurrentPlayListButtonPlayingDetail_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = true;
            UpdateCurrentPlayList();
        }

        private void CurrentPlayListViewPlayingDetail_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = CurrentPlayListViewPlayingDetail.SelectedItem as Music;
            if (selectedMusic != null)
            {
                PlayMusic(selectedMusic);
            }
        }

        private void TopControl_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.TopControlsOpacity = 1.0f;
        }

        private void TopControl_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ViewModel.TopControlsOpacity = 0.0f;
        }

        private void CurrentPlayListTeachingTipPlayingDetailCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTipPlayingDetail.IsOpen = false;
        }

        private void CurrentPlayListTeachingTipCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentPlayListTeachingTip.IsOpen = false;
        }
    }
}
