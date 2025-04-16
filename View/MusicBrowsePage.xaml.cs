using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using static WinUIMusicPlayer.Utils.ToolUtils;
using AppWindow = Microsoft.UI.Windowing.AppWindow;
using Button = Microsoft.UI.Xaml.Controls.Button;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        public MusicPlaybackService musicPlaybackService = new MusicPlaybackService();
        public MainWindow mainWindow;
        public string paramName = "defualt";
        public string pageType = "MusicBrowsePage";
        private bool isMouseOverVolumeSlider = false;
        public string currentAlbumName;
        public string currentArtistName;
        public string currentFolderName;
        public PlayList currentPlayList;
        public int currentPlayListId;
        private bool isMuted = false;
        private DispatcherTimer typingTimer;
        private SystemMediaControlsService systemMediaControlsService;
        private bool isFullScreen = false;
        private AppWindow appWindow;
        private WindowId windowId;
        private bool isMouseOverProgressBar = false;
        private NotificationService notificationService = new NotificationService();
        public MusicBrowsePage()
        {
            this.InitializeComponent();
            InitializeDatabase();
            ProgressSlider.Loaded += ProgressSlider_Loaded;
            this.KeyDown += MusicBrowsePage_KeyDown;
            var window = Window.Current;
            if (window != null)
            {
                window.Closed += Window_Closed;
            }
            AppSettings.OutputSettingsChanged += AppSettings_OutputSettingsChanged!;
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.MusicListLoaded += MainWindow_MusicListLoaded;
                mainWindow.SongCollecionLoaded += MainWindow_SongCollecionLoaded;
                mainWindow.FavourListLoaded += MainWindow_FavourListLoaded;
                mainWindow.PlayMusicListLoaded += MainWindow_PlayMusicListLoaded;
            }
            musicPlaybackService.playingMusic += MusicPlaybackService_playingMusic;
            musicPlaybackService.updatePlayTimeText += MusicPlaybackService_updatePlayTimeText;
            musicPlaybackService.updateProgressSliders += MusicPlaybackService_updateProgressSliders;
            musicPlaybackService.updateProgressMax += MusicPlaybackService_updateProgressMax;
            musicPlaybackService.showMessage += ShowMessage;
            musicPlaybackService.updatePlayPauseButton += MusicPlaybackService_updatePlayPauseButton;
            if (ContentFrame != null)
            {
                switch (AppSettings.DefualtPlayList)
                {
                    case "song":
                        ContentFrame.Navigate(typeof(SongListPage), this);
                        SongButton.FontSize = 26;
                        break;
                    case "album":
                        ContentFrame.Navigate(typeof(AlbumPage), this);
                        AlbumButton.FontSize = 26;
                        break;
                    case "artist":
                        ContentFrame.Navigate(typeof(ArtistPage), this);
                        ArtistButton.FontSize = 26;
                        break;
                    case "folder":
                        ContentFrame.Navigate(typeof(FolderBrowsePage), this);
                        FolderButton.FontSize = 26;
                        break;
                    case "favourite":
                        ContentFrame.Navigate(typeof(FavouritePlayListPage), this);
                        FavouriteButton.FontSize = 26;
                        break;
                    case "playList":
                        ContentFrame.Navigate(typeof(PlayListPage), this);
                        PlayListButton.FontSize = 26;
                        break;
                    default:
                        ContentFrame.Navigate(typeof(SongListPage), this);
                        SongButton.FontSize = 26;
                        break;
                }
            }
            DisableBackButton();
            InitializeTimer();
            InitializeSystemMediaControls();
            InitializeAppWindow();
        }

        private void InitializeAppWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            appWindow = AppWindow.GetFromWindowId(windowId);
        }

        private void InitializeTimer()
        {
            typingTimer = new DispatcherTimer();
            typingTimer.Interval = TimeSpan.FromMilliseconds(400);
            typingTimer.Tick += TypingTimer_Tick;
        }

        private void InitializeSystemMediaControls()
        {
            systemMediaControlsService = new SystemMediaControlsService();

            // 订阅事件
            systemMediaControlsService.PlayRequested += (s, e) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click(null, null);
                });
            };

            systemMediaControlsService.PauseRequested += (s, e) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    PlayButton_Click(null, null);
                });
            };

            systemMediaControlsService.NextTrackRequested += (s, e) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    NextMusicButton_Click(null, null);
                });
            };

            systemMediaControlsService.PreviousTrackRequested += (s, e) =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    LastMusicButton_Click(null, null);
                });
            };
        }

        public void DisableBackButton()
        {
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is SongCollectionPage || ContentFrame.Content is PlayListSongPage)
                {
                    BackButton.IsEnabled = true;
                }
                else
                {
                    BackButton.IsEnabled = false;
                }
            }
        }

        private void MainWindow_PlayMusicListLoaded(object? sender, List<Music> musics)
        {
            musicPlaybackService.musicList = musics;
            //当没有播放列表时，默认播放列表为当前列表
            if (musicPlaybackService.currentPlayingList == null || musicPlaybackService.currentPlayingList.Count == 0)
            {
                musicPlaybackService.currentPlayingList = musics;
            }
            var playListSongPage = ContentFrame.Content as PlayListSongPage;
            if (playListSongPage != null)
            {
                playListSongPage.LoadMusicAsync(musics);
            }
        }

        private void MusicPlaybackService_updateProgressMax(object? sender, double max)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (ProgressSlider != null)
                {
                    ProgressSlider.Maximum = max;
                }
            });
        }

        private void MusicPlaybackService_updatePlayPauseButton(object? sender, string e)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769";
            });
        }

        private async void ShowMessage(object? sender, string message)
        {
            try
            {
                notificationService.SendNotification("错误", message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }
        }

        private void MusicPlaybackService_updateProgressSliders(object? sender, double value)
        {
            try
            {
                if (DispatcherQueue != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ProgressSlider != null)
                        {
                            ProgressSlider.Value = value;
                        }
                    });
                }
            }
            catch (NullReferenceException ex)
            {
                Debug.WriteLine($"更新进度条失败: {ex.Message}");
            }
        }

        private void MusicPlaybackService_updatePlayTimeText(object? sender, string time)
        {
            try
            {
                if (DispatcherQueue != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (PlayTimeTextBlock != null)
                        {
                            PlayTimeTextBlock.Text = time;
                        }
                    });
                }               
            }
            catch (NullReferenceException ex)
            {
                Debug.WriteLine($"更新进度条失败: {ex.Message}");
            }
        }

        private async void MusicPlaybackService_playingMusic(object? sender, Music music)
        {
            await PlayMusic(music);
        }

        private void MainWindow_FavourListLoaded(object? sender, List<Music> musics)
        {
            musicPlaybackService.musicList = musics;
            if (musicPlaybackService.currentPlayingList == null || musicPlaybackService.currentPlayingList.Count == 0)
            {
                musicPlaybackService.currentPlayingList = musics;
            }
            var favouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
            if (favouritePlayListPage != null)
            {
                favouritePlayListPage.LoadMusicAsync(musics);
            }
        }

        private async void MainWindow_SongCollecionLoaded(object sender, List<Music> musics)
        {
            musicPlaybackService.musicList = musics;
            if (musicPlaybackService.currentPlayingList == null || musicPlaybackService.currentPlayingList.Count == 0)
            {
                musicPlaybackService.currentPlayingList = musics;
            }
            var songCollectionPage = ContentFrame.Content as SongCollectionPage;
            if (songCollectionPage != null)
            {
                await songCollectionPage.LoadMusicAsync(musics, pageType);
            }
        }

        private void MainWindow_MusicListLoaded(object sender, List<Music> musics)
        {
            try
            {
                musicPlaybackService.musicList = musics;
                if (musicPlaybackService.currentPlayingList == null || musicPlaybackService.currentPlayingList.Count == 0)
                {
                    musicPlaybackService.currentPlayingList = musics;
                }
                var songListPage = ContentFrame.Content as SongListPage;
                if (songListPage != null)
                {
                    songListPage.LoadMusicAsync(musics);
                }
                var albumBrowsePage = ContentFrame.Content as AlbumPage;
                if (albumBrowsePage != null)
                {
                    albumBrowsePage.LoadAlbumsAsync(musics);
                }
                var artistPage = ContentFrame.Content as ArtistPage;
                if (artistPage != null)
                {
                    artistPage.LoadArtists(musics);
                }
                var folderBrowsePage = ContentFrame.Content as FolderBrowsePage;
                if (folderBrowsePage != null)
                {
                    folderBrowsePage.LoadFolder(musics);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载歌曲列表失败: {ex.Message}");
            }
        }

        public async Task LoadPlayList()
        {
            if (mainWindow != null)
            {
                await mainWindow.LoadPlayList();
            }
        }

        public async void LoadPlayListSong(PlayList playList)
        {
            pageType = "playlist";
            paramName = playList.Name;
            currentPlayList = playList;
            currentPlayListId = playList.Id;
            ResetNavigationButtons();
            PlayListButton.FontSize = 26;
            ContentFrame.Navigate(typeof(PlayListSongPage), this);
            if (mainWindow != null)
            {
                await mainWindow.LoadPlayListMusic(playList.Id, SearchTextBox.Text);
            }
        }
        public async Task LoadMusic()
        {

            if (mainWindow != null)
            {
                await mainWindow.LoadMusicList(SearchTextBox.Text);
            }
        }

        public async Task LoadFavouriteMusic()
        {
            if (mainWindow != null)
            {
                await mainWindow.LoadFavourMusicList(SearchTextBox.Text);
            }
        }

        public async void LoadAlbumMusic(string Album)
        {
            pageType = "album";
            paramName = Album;
            currentAlbumName = Album;
            ResetNavigationButtons();
            AlbumButton.FontSize = 26;
            ContentFrame.Navigate(typeof(SongCollectionPage), this);
            if (mainWindow != null)
            {
                await mainWindow.LoadAlbumMusic(Album, SearchTextBox.Text);
            }
        }

        public async void LoadArtistMusic(string artist)
        {
            pageType = "artist";
            paramName = artist;
            currentArtistName = artist;
            ResetNavigationButtons();
            ArtistButton.FontSize = 26;
            ContentFrame.Navigate(typeof(SongCollectionPage), this);
            if (mainWindow != null)
            {
                await mainWindow.LoadArtistMusic(artist, SearchTextBox.Text);
            }
        }

        public async void LoadFolderMusic(string folder)
        {
            pageType = "folder";
            paramName = folder;
            currentFolderName = folder;
            ContentFrame.Navigate(typeof(SongCollectionPage), this);
            if (mainWindow != null)
            {
                await mainWindow.LoadFolderMusic(folder, SearchTextBox.Text);
            }
        }

        public async Task RemoveMusic(int musicId)
        {
            await MusicDatabaseService.RemoveMusic(musicId);
            await LoadMusic();
        }

        public async Task AddToFavourite(Music music)
        {
            music.isFavorite = !music.isFavorite;
            await MusicDatabaseService.AddToFavourite(music, musicPlaybackService.currentPlayingMusic);
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                if (musicPlaybackService.currentPlayingMusic.Id == music.Id)
                {
                    musicPlaybackService.currentPlayingMusic.isFavorite = music.isFavorite;
                    ((FontIcon)PlayBarFavouriteButton.Content).Glyph = music.isFavorite ? "\ueb52" : "\ueb51";
                }
            }
        }

        public void UpdateFavourtPlaylist(List<Music> newMusicList)
        {
            musicPlaybackService.musicList = newMusicList;
        }

        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            typingTimer.Stop();
            typingTimer.Start();
        }

        private async void TypingTimer_Tick(object sender, object e)
        {
            typingTimer.Stop(); // 定时器触发时停止定时器
            if (ContentFrame != null && ContentFrame.Content != null)
            {
                if (ContentFrame.Content is SongCollectionPage)
                {
                    var page = ContentFrame.Content as SongCollectionPage;
                    if (pageType == "album")
                    {
                        LoadAlbumMusic(paramName);
                    }
                    else if (pageType == "artist")
                    {
                        LoadArtistMusic(paramName);
                    }
                    else if (pageType == "folder")
                    {
                        LoadFolderMusic(paramName);
                    }
                }
                else if (ContentFrame.Content is FavouritePlayListPage)
                {
                    await LoadFavouriteMusic();
                }
                else if (ContentFrame.Content is PlayListSongPage)
                {
                    LoadPlayListSong(currentPlayList);
                }
                else
                {
                    await LoadMusic();
                }
            }
        }

        private void MusicBrowsePage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    AdjustPlaybackPosition(-5);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Right:
                    AdjustPlaybackPosition(5);
                    e.Handled = true;
                    break;
            }
        }

        private void AdjustPlaybackPosition(int seconds)
        {
            ProgressSlider.Value = musicPlaybackService.AdjustPlaybackPosition(seconds);
        }

        private void AppSettings_OutputSettingsChanged(object sender, EventArgs e)
        {
            musicPlaybackService.ChangingSetting();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                switch (pageType)
                {
                    case "album":
                        ContentFrame.Navigate(typeof(AlbumPage), this);
                        break;
                    case "artist":
                        ContentFrame.Navigate(typeof(ArtistPage), this);
                        break;
                    case "folder":
                        ContentFrame.Navigate(typeof(FolderBrowsePage), this);
                        break;
                    default:
                        break;
                }
            }
            if (ContentFrame.Content is PlayListSongPage)
            {
                ContentFrame.Navigate(typeof(PlayListPage), this);
            }
        }

        private void ResetNavigationButtons()
        {
            SongButton.FontSize = 20;
            AlbumButton.FontSize = 20;
            ArtistButton.FontSize = 20;
            FolderButton.FontSize = 20;
            FavouriteButton.FontSize = 20;
            PlayListButton.FontSize = 20;
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            ResetNavigationButtons();
            if (button != null)
            {
                button.FontSize = 26;
                switch (button.Tag.ToString())
                {
                    case "Song":
                        ContentFrame.Navigate(typeof(SongListPage), this);
                        break;
                    case "Album":
                        if (!string.IsNullOrEmpty(currentAlbumName))
                        {
                            LoadAlbumMusic(currentAlbumName);
                        }
                        else
                        {
                            ContentFrame.Navigate(typeof(AlbumPage), this);
                        }
                        break;
                    case "Artist":
                        if (!string.IsNullOrEmpty(currentArtistName))
                        {
                            LoadArtistMusic(currentArtistName);
                        }
                        else
                        {
                            ContentFrame.Navigate(typeof(ArtistPage), this);
                        }
                        break;
                    case "Folder":
                        if (!string.IsNullOrEmpty(currentFolderName))
                        {
                            LoadFolderMusic(currentFolderName);
                        }
                        else
                        {
                            ContentFrame.Navigate(typeof(FolderBrowsePage), this);
                        }
                        break;
                    case "Favourite":
                        ContentFrame.Navigate(typeof(FavouritePlayListPage), this);
                        break;
                    case "PlayList":
                        if (currentPlayList != null)
                        {
                            LoadPlayListSong(currentPlayList);
                        }
                        else
                        {
                            ContentFrame.Navigate(typeof(PlayListPage), this);
                        }
                        break;
                }
            }
            DisableBackButton();
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            musicPlaybackService.DisposeAudio();
        }

        private async Task LoadPlayState()
        {
            musicPlaybackService.currentPlayMode = AppData.PlayMode;
            musicPlaybackService.lastPlayedMusicId = AppData.LastPlayedMusicId;
            musicPlaybackService.volume = AppData.Volume;
            VolumeSlider.Value = musicPlaybackService.volume * 100;
            musicPlaybackService.currentPlayingMusic = await MusicDatabaseService.LoadCurrentPlayingMusic(musicPlaybackService.lastPlayedMusicId);
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                UpdatePlayBar(musicPlaybackService.currentPlayingMusic);
                await LoadCover(musicPlaybackService.currentPlayingMusic);
            }
            UpdatePlayModeIcon();
            musicPlaybackService.isInitializing = false;
        }
        private async void AddPlayList_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog contentDialog = new ContentDialog
            {
                Title = "添加播放列表",
                Content = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "请输入播放列表名称" },
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot
            };
            ContentDialogResult result = await contentDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.UI.Xaml.Controls.TextBox textBox = (Microsoft.UI.Xaml.Controls.TextBox)contentDialog.Content;
                string playlistName = textBox.Text;
                if (!string.IsNullOrEmpty(playlistName))
                {
                    var newPlaylist = new PlayList { Name = playlistName };
                    await MusicDatabaseService.InsertPlayList(newPlaylist);
                    await LoadPlayList();
                }
            }
        }

        private async void PlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            musicPlaybackService.SwitchPlayMode();
            await musicPlaybackService.SavePlayState();
            UpdatePlayModeIcon();
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            if (appWindow != null)
            {
                if (isFullScreen)
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.Default);
                    FullScreenIcon.Glyph = "\uE740";
                }
                else
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    FullScreenIcon.Glyph = "\uE73F";
                }
                isFullScreen = !isFullScreen;
            }

        }

        private async void NextMusicButton_Click(object sender, RoutedEventArgs e)
        {
            musicPlaybackService.isManualSelect = true;
            await musicPlaybackService.AutoPlayNextTrack();
            musicPlaybackService.isManualSelect = false;
        }

        private async void LastMusicButton_Click(object sender, RoutedEventArgs e)
        {
            musicPlaybackService.isManualSelect = true;
            await PlayLastTrack();
            musicPlaybackService.isManualSelect = false;
        }

        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustPlaybackPosition(5);
        }

        private void FastBackwardButton_Click(object sender, RoutedEventArgs e)
        {
            AdjustPlaybackPosition(-5);
        }

        private async Task PlayLastTrack()
        {
            int index = musicPlaybackService.currentPlayingList.IndexOf(musicPlaybackService.currentPlayingMusic);
            if (index > 0)
            {
                await PlayMusic(musicPlaybackService.currentPlayingList[index - 1]);
            }
            else if (index == 0 && musicPlaybackService.currentPlayingList.Count > 1)
            {
                await PlayMusic(musicPlaybackService.currentPlayingList[musicPlaybackService.musicList.Count - 1]);

            }
        }

        private void UpdatePlayModeIcon()
        {
            switch (musicPlaybackService.currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    PlayModeIcon.Glyph = "\ue8ed"; // 单曲循环图标
                    break;
                case PlayMode.ListLoop:
                    PlayModeIcon.Glyph = "\ue8ee"; // 列表循环图标
                    break;
                case PlayMode.RandomLoop:
                    PlayModeIcon.Glyph = "\ue8b1"; // 随机循环图标
                    break;
            }
        }

        private async void InitializeDatabase()
        {
            musicPlaybackService.isInitializing = true;
            _ = LoadPlayState();
            _ = LoadMusic();
            musicPlaybackService.OutputDeviceChange();
            PlayTimeTextBlock.Text = "00:00/00:00";
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            musicPlaybackService.PlayButton();
            UpdatePlayPauseButtonIcon();
            systemMediaControlsService.UpdateSystemMediaControlsState();
        }

        private void UpdatePlayPauseButtonIcon()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (AppSettings.isPlaying)
                {
                    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769"; // 暂停图标
                }
                else
                {
                    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE768"; // 播放图标
                }
            });
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            musicPlaybackService.StopPlaying();
            UpdatePlayPauseButtonIcon();
            musicPlaybackService.Reset();
            ProgressSlider.Value = 0;
        }

        private async void PlayBarFavouriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (musicPlaybackService.currentPlayingMusic != null)
            {
                ((FontIcon)PlayBarFavouriteButton.Content).Glyph = !musicPlaybackService.currentPlayingMusic.isFavorite ? "\ueb52" : "\ueb51";
                await AddToFavourite(musicPlaybackService.currentPlayingMusic);
                NotifySubPageUpdateFavouriteState();
            }
        }

        private void NotifySubPageUpdateFavouriteState()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                songCollectionPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
            if (ContentFrame.Content is SongListPage)
            {
                var songListPage = ContentFrame.Content as SongListPage;
                songListPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
            if (ContentFrame.Content is FavouritePlayListPage)
            {
                var favouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                favouritePlayListPage.UpdateFavouriteMusic(musicPlaybackService.currentPlayingMusic);
            }
        }

        private async Task LoadCover(Music music)
        {
            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
            {
                AlbumCoverImage.Source = cachedCover;
            }
            else
            {
                AlbumCoverImage.Source = await GetImageFromMusic(music);
            }
        }

        private void UpdatePlayBar(Music music)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                MusicTitleTextBlock.Text = music.Title;
                MusicAlbumTextBlock.Text = music.Album;
                MusicAuthorTextBlock.Text = music.Author;
                MusicInfoTextBlock.Text = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                ((FontIcon)PlayBarFavouriteButton.Content).Glyph = musicPlaybackService.currentPlayingMusic.isFavorite ? "\ueb52" : "\ueb51";
                HRImage.Source = null;
                if (music.SampleRate >= 48000 && music.BitDepth >= 24)
                {
                    var bitmapImage = new BitmapImage(new Uri("ms-appx:///Assets/hr.png"));
                    HRImage.Source = bitmapImage;
                }
            });
        }

        private void UpdateViewList(Music music)
        {
            DispatcherQueue.TryEnqueue(async () =>
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
                await LoadCover(music);
                systemMediaControlsService.UpdateSystemMediaControlsState();
                _ = systemMediaControlsService.UpdateMediaInfo(music.Title, music.Author, music.Album, AlbumCoverImage);
            });
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            musicPlaybackService.currentPlayingMusic = music;
            UpdatePlayBar(music);
            UpdateViewList(music);
            await musicPlaybackService.PlayMusic(music, currentPos, isSettingChanged);
        }

        private void SortByComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                var sortOrder = selectedItem.Tag.ToString();
                if (ContentFrame != null && ContentFrame.Content != null)
                {
                    if (ContentFrame.Content is SongCollectionPage)
                    {
                        var page = ContentFrame.Content as SongCollectionPage;
                        page.SortMusicList(sortOrder, pageType);
                    }
                    if (ContentFrame.Content is SongListPage)
                    {
                        var page = ContentFrame.Content as SongListPage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FavouritePlayListPage)
                    {
                        var page = ContentFrame.Content as FavouritePlayListPage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                    if (ContentFrame.Content is AlbumPage)
                    {
                        var page = ContentFrame.Content as AlbumPage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                    if (ContentFrame.Content is ArtistPage)
                    {
                        var page = ContentFrame.Content as ArtistPage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                    if (ContentFrame.Content is FolderBrowsePage)
                    {
                        var page = ContentFrame.Content as FolderBrowsePage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                    if (ContentFrame.Content is PlayListSongPage)
                    {
                        var page = ContentFrame.Content as PlayListSongPage;
                        if (page != null)
                        {
                            page.SortMusicList(sortOrder);
                        }
                    }
                }
            }
        }

        private void VolumeSliderIconButton_Click(object sender, RoutedEventArgs e)
        {
            isMuted = !isMuted;
            if (isMuted)
            {
                VolumeSliderIcon.Glyph = "\ue74f";
                musicPlaybackService.volume = 0;
                if (musicPlaybackService.sampleChannel != null)
                {
                    musicPlaybackService.sampleChannel.Volume = 0;
                }
                if (musicPlaybackService.waveChannel != null)
                {
                    musicPlaybackService.waveChannel.Volume = 0;
                }
            }
            else
            {
                VolumeIconChange((int)VolumeSlider.Value);
                musicPlaybackService.volume = (float)VolumeSlider.Value / 100;
                if (musicPlaybackService.sampleChannel != null)
                {
                    musicPlaybackService.sampleChannel.Volume = (float)VolumeSlider.Value / 100;
                }
                if (musicPlaybackService.waveChannel != null)
                {
                    musicPlaybackService.waveChannel.Volume = (float)VolumeSlider.Value / 100;
                }
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            musicPlaybackService.volume = (float)e.NewValue / 100;
            if (musicPlaybackService.sampleChannel != null)
            {
                musicPlaybackService.sampleChannel.Volume = musicPlaybackService.volume;
            }
            if (musicPlaybackService.waveChannel != null)
            {
                musicPlaybackService.waveChannel.Volume = musicPlaybackService.volume;
            }
            VolumeIconChange((int)e.NewValue);
            _ = musicPlaybackService.SavePlayState();
        }

        private void VolumeIconChange(int volume)
        {
            if (volume > 66)
            {
                VolumeSliderIcon.Glyph = "\ue995";
            }
            else if (volume > 33)
            {
                VolumeSliderIcon.Glyph = "\ue994";
            }
            else if (volume > 0)
            {
                VolumeSliderIcon.Glyph = "\uE993";
            }
            else
            {
                VolumeSliderIcon.Glyph = "\uE992";
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
            isMouseOverProgressBar = true;
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverProgressBar = false;
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
                    AdjustVolume(1);
                }
                else if (delta < 0)
                {
                    AdjustVolume(-1);
                }
                e.Handled = true;
            }
        }

        private void AdjustVolume(int delta)
        {
            double newVolume = VolumeSlider.Value + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            VolumeSlider.Value = newVolume;
        }

        private void AuthorTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string artist = textBlock.Text;
                LoadArtistMusic(artist);
            }
        }

        private void AlbumTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                string albumName = textBlock.Text;
                LoadAlbumMusic(albumName);
            }
        }

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            musicPlaybackService.isUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            musicPlaybackService.isUserDraggingProgressSlider = false;

            if (AppSettings.isPlaying)
            {
                if (musicPlaybackService.currentPlayingMusic.Extension.ToLower() == "flac" && musicPlaybackService.waveChannel != null)
                {
                    // 对于FLAC文件，设置waveChannel的位置
                    double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value,
                        (double)musicPlaybackService.waveChannel.Length / musicPlaybackService.waveChannel.WaveFormat.AverageBytesPerSecond));

                    musicPlaybackService.waveChannel.Position = (long)(newPosition * musicPlaybackService.waveChannel.WaveFormat.AverageBytesPerSecond);
                }
                else if (musicPlaybackService.fFmpegAudioReader != null)
                {
                    // 对于其他格式，设置audioFileReader的位置
                    double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, musicPlaybackService.fFmpegAudioReader.TotalTime.TotalSeconds));
                    musicPlaybackService.fFmpegAudioReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
                }
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isMouseOverProgressBar)
            {
                if (!musicPlaybackService.isUserDraggingProgressSlider && AppSettings.isPlaying)
                {
                    double currentPlayPosition = 0;
                    if (musicPlaybackService.waveChannel != null && musicPlaybackService.currentPlayingMusic.Extension.ToLower() == "flac")
                    {
                        currentPlayPosition = musicPlaybackService.waveChannel.CurrentTime.TotalSeconds;
                        if (Math.Abs(e.NewValue - currentPlayPosition) > 2.0)
                        {
                            musicPlaybackService.waveChannel.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
                        }
                    }
                    else if (musicPlaybackService.fFmpegAudioReader != null)
                    {
                        currentPlayPosition = musicPlaybackService.fFmpegAudioReader.CurrentTime.TotalSeconds;
                        if (Math.Abs(e.NewValue - currentPlayPosition) > 2.0)
                        {
                            musicPlaybackService.fFmpegAudioReader.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
                        }
                    }
                }
            }
        }
    }
}
