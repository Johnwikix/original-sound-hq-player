using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
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
        //public MusicPlaybackService musicPlaybackService;
        public MainWindow mainWindow;
        public string paramName = "defualt";
        public string pageType = "MusicBrowsePage";
        public System.Type currentPage = typeof(SongListPage);
        private bool isMouseOverVolumeSlider = false;
        public string currentAlbumName;
        public string currentArtistName;
        public string currentFolderName;
        public PlayList currentPlayList;
        public int currentPlayListId;
        private DispatcherTimer typingTimer;
        //private bool isFullScreen = false;
        //private AppWindow appWindow = App.MainWindow.AppWindow;      
        private NotificationService notificationService;
        public EventHandler refreshSong;
        public EventHandler refreshPage;
        public EventHandler<PlayList> addPlayListEvent;
        public int previousSelectedIndex = 0;
        private bool isInPlayingDetailMode = false;
        private readonly INavigationService _navigationService;
        public event EventHandler clearUsbDeviceMusicList;
        public event EventHandler refreshUsbDeviceMusicList;
        private EqualizerDialog equalizerDialog;
        private bool isSearching = false;
        private string lastSearchText = string.Empty;
        
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
            this.KeyDown += MusicBrowsePage_KeyDown;
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
                DispatcherQueue.TryEnqueue(() =>
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
            ViewModel.OnFileChanged(null, null);
            ViewModel.IsInitialized = true;
            //TODO 波形可视化
         
        }

        private void MainWindow_updateSelectSection(object? sender, EventArgs e)
        {
            SelectBarItem(AppSettings.DefualtPlayList);
        }
        
        public void UpdateCurrentLyricIndex(int currentIndex)
        {
            if (ViewModel.LastLyricIndex == currentIndex || !isInPlayingDetailMode)
                return;
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // 创建当前歌词的副本
                    for (int i = 0; i < ViewModel.UILyrics.Count; i++)
                    {
                        // 获取旧歌词
                        var lyric = ViewModel.UILyrics[i];
                        ViewModel.UILyrics[i].IsCurrent = (i == currentIndex);
                    }
                    // 滚动到当前歌词
                    if (currentIndex >= 0 && currentIndex < ViewModel.UILyrics.Count)
                    {
                        LyricsListView.ScrollIntoView(ViewModel.UILyrics[currentIndex]);
                        await Task.Delay(50);
                        // 获取目标项的容器
                        if (currentIndex >= 0 && currentIndex < ViewModel.UILyrics.Count)
                        {
                            var container = LyricsListView.ContainerFromItem(ViewModel.UILyrics[currentIndex]) as ListViewItem;
                            if (container != null)
                            {
                                // 计算需要滚动的位置，使项目居中
                                var scrollViewer = FindVisualChild<ScrollViewer>(LyricsListView);
                                if (scrollViewer != null)
                                {
                                    // 计算项目在ListView中的位置
                                    var transform = container.TransformToVisual(LyricsListView);
                                    var itemPosition = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                                    // 计算目标项目与ListView中心的偏移量
                                    var itemOffset = itemPosition.Y + (container.ActualHeight / 2);
                                    var listViewCenter = scrollViewer.ViewportHeight / 2;
                                    var scrollOffset = itemOffset - listViewCenter;
                                    scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + scrollOffset, null, false);
                                }
                            }
                        }
                    }
                    ViewModel.LastLyricIndex = currentIndex;
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), $"{ToolUtils.GetString("UpdatingLyricsFailed")}: {ex.Message}");
                }
            });
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
                    selectPage.SelectedItem = selectorBarItem;
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
      

        private async void UsbDeviceCombox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppData.usbStorageDevice = null;
            AppData.musicOnUsbDevice.Clear();
            clearUsbDeviceMusicList?.Invoke(this, EventArgs.Empty);
            if (UsbDeviceCombox.SelectedItem is UsbStorageDevice usbStorageDevice)
            {
                Debug.WriteLine($"USB设备已选择: {usbStorageDevice.UniqueId}");
                AppData.usbStorageDevice = usbStorageDevice;
                List<UsbDeviceMusic> usbDeviceMusics = await MusicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
                if (usbDeviceMusics != null && usbDeviceMusics.Count > 0)
                {
                    // 检查是否需要重新扫描
                    DateTime startTime = DateTime.Now;
                    UsbDeviceSubFolderRescan usbDeviceSubFolderRescan = new UsbDeviceSubFolderRescan();
                    await usbDeviceSubFolderRescan.UsbDeviceSubFolderAutoScan(usbDeviceMusics, usbStorageDevice.Path, usbStorageDevice.UniqueId);
                    Debug.WriteLine($"UsbDeviceSubFolderAutoScan完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
                    AppData.musicOnUsbDevice = await MusicDatabaseService.GetUsbDeviceMusics(usbStorageDevice.UniqueId);
                    Debug.WriteLine($"USB设备扫描完成,耗时:{(DateTime.Now - startTime).TotalSeconds}秒");
                }
                else
                {
                    // 读取USB设备中的音乐文件
                    string folderPath = Path.Combine(usbStorageDevice.Path, "MUSIC");
                    if (Directory.Exists(folderPath))
                    {
                        AppData.musicOnUsbDevice = await MusicDatabaseService.RescanUsbDeviceFolderByPath(usbDeviceMusics, usbStorageDevice.UniqueId, folderPath, false);
                    }
                    else
                    {
                        notificationService.SendNotification(ToolUtils.GetString("Error"), ToolUtils.GetString("NoMusicInUSBDevice"));
                    }
                }
                refreshUsbDeviceMusicList?.Invoke(this, EventArgs.Empty);
            }
        }

        public void DisableBackButton()
        {
            if (currentPage == typeof(SongCollectionPage) || currentPage == typeof(PlayListSongPage))
            {
                BackButton.IsEnabled = true;
            }
            else
            {
                BackButton.IsEnabled = false;
            }
        }

        public async void LoadPlayListSong(PlayList playList)
        {
            pageType = "playlist";
            paramName = playList.Name;
            currentPlayList = playList;
            currentPlayListId = playList.Id;
            currentPage = typeof(PlayListSongPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(),AppSettings.DrillInAnimationTime);
        }

        public async void LoadAlbumMusic(string Album)
        {
            pageType = "album";
            paramName = Album;
            currentAlbumName = Album;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(),AppSettings.DrillInAnimationTime);
        }

        public void SelectBarAlbum(string Album)
        {
            pageType = "album";
            paramName = Album;
            currentAlbumName = Album;
            currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is AlbumPage)
                {
                    _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("album");
                }
            }
        }

        public async void LoadArtistMusic(string artist)
        {
            pageType = "artist";
            paramName = artist;
            currentArtistName = artist;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
        }

        public void SelectBarArtist(string artist)
        {
            pageType = "artist";
            paramName = artist;
            currentArtistName = artist;
            currentPage = typeof(SongCollectionPage);
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is ArtistPage)
                {
                    _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
                else
                {
                    SelectBarItem("artist");
                }
            }
        }

        public async void LoadFolderMusic(string folder)
        {
            pageType = "folder";
            paramName = folder;
            currentFolderName = folder;
            currentPage = typeof(SongCollectionPage);
            _navigationService.Navigate(currentPage, this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
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

        private void MusicBrowsePage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    ViewModel.AdjustPlaybackPosition(-5);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Right:
                    ViewModel.AdjustPlaybackPosition(5);
                    e.Handled = true;
                    break;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                switch (pageType)
                {
                    case "album":
                        currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(AlbumPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "artist":
                        currentPage = typeof(ArtistPage);
                        _navigationService.Navigate(typeof(ArtistPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    case "folder":
                        currentPage = typeof(AlbumPage);
                        _navigationService.Navigate(typeof(FolderBrowsePage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                        break;
                    default:
                        break;
                }
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                currentPage = typeof(PlayListPage);
                _navigationService.Navigate(typeof(PlayListPage), this, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
            DisableBackButton();
        }

        private void SelectPage_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            DateTime startTime = DateTime.Now;
            //ResetNavigationButtons();
            SelectorBarItem selectedItem = sender.SelectedItem;
            //selectedItem.FontSize = 26;
            int currentSelectedIndex = sender.Items.IndexOf(selectedItem);
            currentPage = typeof(SongListPage);
            switch (selectedItem.Name)
            {
                case "Song":
                    currentPage = typeof(SongListPage);
                    break;
                case "Album":
                    if (!string.IsNullOrEmpty(currentAlbumName))
                    {
                        pageType = "album";
                        paramName = currentAlbumName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(AlbumPage);
                    }
                    break;
                case "Artist":
                    if (!string.IsNullOrEmpty(currentArtistName))
                    {
                        pageType = "artist";
                        paramName = currentArtistName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(ArtistPage);
                    }
                    break;
                case "Folder":
                    if (!string.IsNullOrEmpty(currentFolderName))
                    {
                        pageType = "folder";
                        paramName = currentFolderName;
                        currentPage = typeof(SongCollectionPage);
                    }
                    else
                    {
                        currentPage = typeof(FolderBrowsePage);
                    }
                    break;
                case "Favourite":
                    currentPage = typeof(FavouritePlayListPage);
                    break;
                case "PlayList":
                    if (currentPlayList != null)
                    {
                        pageType = "playlist";
                        paramName = currentPlayList.Name;
                        currentPlayListId = currentPlayList.Id;
                        currentPage = typeof(PlayListSongPage);
                    }
                    else
                    {
                        currentPage = typeof(PlayListPage);
                    }
                    break;
            }
            var slideNavigationTransitionEffect = currentSelectedIndex - previousSelectedIndex > 0 ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft;
            _navigationService.Navigate(currentPage, this, new SlideNavigationTransitionInfo() { Effect = slideNavigationTransitionEffect },AppSettings.SlideAnimationTime);
            previousSelectedIndex = currentSelectedIndex;
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
                                CurrentPlayListView.ScrollIntoView(selectedMusic);
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
                        page.SortMusicList(AppData.sortOrder, pageType);
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
                    if (ContentFrame.Content is ArtistPage)
                    {
                        var page = ContentFrame.Content as ArtistPage;
                        if (page != null)
                        {
                            page.SortMusicList(AppData.sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FolderBrowsePage)
                    {
                        var page = ContentFrame.Content as FolderBrowsePage;
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
            if (ViewModel._musicPlaybackService.waveChannel != null)
            {
                double newPosition = Math.Max(0, Math.Min(ViewModel.ProgressSlider, ViewModel._musicPlaybackService.waveChannel.TotalTime.TotalSeconds));
                ViewModel._musicPlaybackService.waveChannel.CurrentTime = TimeSpan.FromSeconds(newPosition);
            }
        }

        private async void AlbumCoverImage_Click(object sender, RoutedEventArgs e)
        {
            await ShowPlayingDetail();
        }

        public async Task ShowPlayingDetail() {
            if (!isInPlayingDetailMode) {
                isInPlayingDetailMode = true;
                if (ViewModel.CurrentPlayingMusic!=null && AlbumCoverImage!=null)
                {
                    ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("CoverToDetail", AlbumCoverImage);
                    TopPanel.Visibility = Visibility.Collapsed;
                    ContentFrame.Visibility = Visibility.Collapsed;
                    AlbumCoverAuthorTitleModel.Visibility = Visibility.Collapsed;
                    ViewModel.InfoBarIsOpen = false;
                    PlayingDetail.Visibility = Visibility.Visible;
                    ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("CoverToDetail");
                    if (animation != null)
                    {
                        animation.Configuration = new DirectConnectedAnimationConfiguration();
                        animation.TryStart(PlayingDetailAlbumCoverImageGrid);
                    }
                }                
            }            
        }

        private void CancelPlayingDetailButton_Click(object sender, RoutedEventArgs e)
        {
            isInPlayingDetailMode = false;
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("DetailToCover", PlayingDetailAlbumCoverImageGrid);
            TopPanel.Visibility = Visibility.Visible;
            ContentFrame.Visibility = Visibility.Visible;
            PlayingDetail.Visibility = Visibility.Collapsed;
            AlbumCoverAuthorTitleModel.Visibility = Visibility.Visible;
            ConnectedAnimation animation = ConnectedAnimationService.GetForCurrentView().GetAnimation("DetailToCover");
            if (animation != null)
            {
                animation.Configuration = new BasicConnectedAnimationConfiguration();
                animation.TryStart(AlbumCoverImage);
            }
        }

        private void EqualizerButton_Click(object sender, RoutedEventArgs e)
        {
            equalizerDialog.RequestedTheme = AppSettings.elementTheme;
            equalizerDialog.XamlRoot = this.XamlRoot;
            _=equalizerDialog.ShowAsync();           
        }

        private void VolumeSlider_DragEnter(object sender, DragEventArgs e)
        {

        }

        private void VolumeSlider_DragLeave(object sender, DragEventArgs e)
        {

        }
    }
}
