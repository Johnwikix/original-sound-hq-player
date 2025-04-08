using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
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
        private SQLiteAsyncConnection dbConnection;
        public Music currentPlayingMusic;
        public List<Music> currentPlayingList;
        private IWavePlayer waveOut;
        private AudioFileReader audioFileReader;
        private float volume = 0.5f;
        private bool isPlaying;
        private System.Timers.Timer progressTimer;
        private bool isUserDraggingProgressSlider = false;
        private PlayMode currentPlayMode = PlayMode.ListLoop;
        private int? lastPlayedMusicId;
        private bool isManualSelect = false;
        private bool isPausing = false;
        private bool isSettingsChangeStop = false;
        private List<Music> musicList;
        public MainWindow mainWindow;
        private MMDevice selectedDevice = null;
        private string paramName = "defualt";
        private string pageType = "MusicBrowsePage";
        private bool isMouseOverVolumeSlider = false;
        private bool isInitializing = true;
        public string currentAlbumName;
        public string currentArtistName;
        public string currentFolderName;

        public MusicBrowsePage()
        {
            this.InitializeComponent();
            InitializeDatabase();
            progressTimer = new System.Timers.Timer(1000);
            progressTimer.Elapsed += ProgressTimer_Elapsed;
            ProgressSlider.Loaded += ProgressSlider_Loaded;

            // 添加键盘事件处理
            this.KeyDown += MusicBrowsePage_KeyDown;

            // 订阅窗口关闭事件
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
            }
            if (ContentFrame != null)
            {
                switch (AppSettings.DefualtPlayList)
                {
                    case "歌曲":
                        ContentFrame.Navigate(typeof(SongListPage), this);
                        SongButton.FontSize = 26;
                        break;
                    case "专辑":
                        ContentFrame.Navigate(typeof(AlbumBrowsePage), this);
                        AlbumButton.FontSize = 26;
                        break;
                    case "艺术家":
                        ContentFrame.Navigate(typeof(ArtistPage), this);
                        ArtistButton.FontSize = 26;
                        break;
                    case "文件夹":
                        ContentFrame.Navigate(typeof(FolderBrowsePage), this);
                        FolderButton.FontSize = 26;
                        break;
                    case "最爱":
                        ContentFrame.Navigate(typeof(FavouritePlayListPage), this);
                        FavouriteButton.FontSize = 26;
                        break;
                    default:
                        ContentFrame.Navigate(typeof(SongListPage), this);
                        SongButton.FontSize = 26;
                        break;
                }
            }
        }

        private void MainWindow_FavourListLoaded(object? sender, List<Music> musics)
        {
            musicList = musics;
            if (currentPlayingList == null || currentPlayingList.Count == 0) {
                currentPlayingList = musics;
            }
            var favouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
            if (favouritePlayListPage != null)
            {
                favouritePlayListPage.LoadMusicAsync(musics);
            }
        }

        private async void MainWindow_SongCollecionLoaded(object sender, List<Music> musics)
        {
            musicList = musics;
            if (currentPlayingList.Count == 0)
            {
                currentPlayingList = musics;
            }
            var songCollectionPage = ContentFrame.Content as SongCollectionPage;
            if (songCollectionPage != null)
            {
                await songCollectionPage.LoadMusicAsync(musics,pageType);
            }
        }

        private void MainWindow_MusicListLoaded(object sender, List<Music> musics)
        {
            try
            {
                musicList = musics;
                if (currentPlayingList ==null || currentPlayingList.Count == 0)
                {
                    currentPlayingList = musics;
                }
                var songListPage = ContentFrame.Content as SongListPage;
                if (songListPage != null)
                {
                    songListPage.LoadMusicAsync(musics);
                }
                var albumBrowsePage = ContentFrame.Content as AlbumBrowsePage;
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
            await dbConnection.DeleteAsync<Music>(musicId);
            await LoadMusic();
        }

        public async Task AddToFavourite(Music music)
        {
            Music lastFavouriteMusic = await dbConnection.Table<Music>()
                                          .Where(m => m.isFavorite)
                                          .OrderByDescending(m => m.Order)
                                          .FirstOrDefaultAsync();
            if (lastFavouriteMusic != null)
            {
                if (music.isFavorite) {
                    music.Order = 0;
                }
                else
                {
                    music.Order = lastFavouriteMusic.Order + 1;
                }   
            }
            else {
                music.Order = 1;
            }
            bool isFavourite = !music.isFavorite;
            if (currentPlayingMusic.Id == music.Id)
            {
                currentPlayingMusic.isFavorite = isFavourite;
                ((FontIcon)PlayBarFavouriteButton.Content).Glyph = isFavourite ? "\ueb52" : "\ueb51";
            }
            music.isFavorite = isFavourite;
            await dbConnection.UpdateAsync(music);
        }

        public void UpdateFavourtPlaylist(List<Music> newMusicList)
        {
            musicList = newMusicList;
        }

        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
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
                }
                else if (ContentFrame.Content is FavouritePlayListPage)
                {
                    await LoadFavouriteMusic();
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
            if (audioFileReader != null && isPlaying)
            {
                double newPosition = audioFileReader.CurrentTime.TotalSeconds + seconds;
                newPosition = Math.Max(0, Math.Min(newPosition, audioFileReader.TotalTime.TotalSeconds));
                audioFileReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
                ProgressSlider.Value = newPosition;
            }
        }        

        private void AppSettings_OutputSettingsChanged(object sender, EventArgs e)
        {
            // 如果当前正在播放，停止播放并重新初始化音频资源
            if (isPlaying)
            {
                isSettingsChangeStop = true;
                TimeSpan currentPos = audioFileReader.CurrentTime;

                if (progressTimer != null)
                {
                    progressTimer.Stop();
                }

                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }

                if (audioFileReader != null)
                {
                    ResumeMusic();
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is SongCollectionPage) {
                switch (pageType) {
                    case "album":
                        ContentFrame.Navigate(typeof(AlbumBrowsePage), this);
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
        }

        private void ResetNavigationButtons()
        {
            SongButton.FontSize = 20;
            AlbumButton.FontSize = 20;
            ArtistButton.FontSize = 20;
            FolderButton.FontSize = 20;
            FavouriteButton.FontSize = 20;
        }

        private async void NavigationButton_Click(object sender, RoutedEventArgs e)
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
                            ContentFrame.Navigate(typeof(AlbumBrowsePage), this);
                        }
                        break;
                    case "Artist":
                        if (!string.IsNullOrEmpty(currentArtistName)) {
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
                }
            }
        }
        //出于某些原因audioFileReader不能被销毁
        private void ResumeMusic()
        {
            try
            {
                OutputDeviceChange();
                SelectOutputDevice();
                waveOut.Init(audioFileReader);
                waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                waveOut.Play();
                isPlaying = true;
                progressTimer.Start();
            }
            catch (Exception ex)
            {
                ShowMessage($"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }
        }

        private void OutputDeviceChange() {
            MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            //selectedDevice = null;
            if (AppSettings.DeviceName != null)
            {
                foreach (var device in devices)
                {
                    if (device.FriendlyName == AppSettings.DeviceName)
                    {
                        selectedDevice = device;
                        break;
                    }
                }
            }
            else {
                selectedDevice = devices[0];
            }
        }

        private void SelectOutputDevice()
        {
            
            switch (AppSettings.OutputMode)
            {
                case "WaveOut":
                    waveOut = new WaveOutEvent();
                    break;
                case "WasapiShared":
                    waveOut = new WasapiOut(selectedDevice, AudioClientShareMode.Shared, false, AppSettings.Latency);
                    break;
                case "WasapiExclusive":
                    waveOut = new WasapiOut(selectedDevice, AudioClientShareMode.Exclusive, true, AppSettings.Latency);
                    break;
                case "DirectSound":
                    waveOut = new DirectSoundOut(AppSettings.Latency);
                    break;
                default:
                    waveOut = new WaveOutEvent();
                    break;
            }
            if (waveOut is WaveOutEvent defaultWaveOutEvent)
            {
                defaultWaveOutEvent.DesiredLatency = AppSettings.Latency;
                defaultWaveOutEvent.NumberOfBuffers = 3;
            }
        }
        private void Window_Closed(object sender, WindowEventArgs args)
        {
            // 停止定时器
            if (progressTimer != null)
            {
                progressTimer.Stop();
                progressTimer.Elapsed -= ProgressTimer_Elapsed;
                progressTimer.Dispose();
                progressTimer = null;
            }

            // 停止并释放 waveOut
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            // 释放 audioFileReader
            if (audioFileReader != null)
            {
                audioFileReader.Dispose();
                audioFileReader = null;
            }
        }

        private async void ShowMessage(string message)
        {
            try
            {
                ContentDialog contentDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await contentDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
            }
        }

        private async Task LoadPlayState()        {
            
            currentPlayMode = AppData.PlayMode;
            lastPlayedMusicId = AppData.LastPlayedMusicId;
            volume = AppData.Volume;
            VolumeSlider.Value = volume * 100;
            currentPlayingMusic = await dbConnection.Table<Music>().Where(m => m.Id == lastPlayedMusicId).FirstOrDefaultAsync();
            if (currentPlayingMusic != null)
            {
                MusicTitleTextBlock.Text = currentPlayingMusic.Title;
                MusicAuthorTextBlock.Text = currentPlayingMusic.Author;
                MusicInfoTextBlock.Text = $"{currentPlayingMusic.Extension} {currentPlayingMusic.SampleRate}Hz {currentPlayingMusic.BitDepth}bit {currentPlayingMusic.BitRate}kbps";
                ((FontIcon)PlayBarFavouriteButton.Content).Glyph = currentPlayingMusic.isFavorite ? "\ueb52" : "\ueb51";
                HRImage.Source = null;
                if (currentPlayingMusic.SampleRate >= 48000 && currentPlayingMusic.BitDepth >= 24)
                {
                    var bitmapImage = new BitmapImage(new Uri("ms-appx:///Assets/hr.png"));
                    HRImage.Source = bitmapImage;
                }
                await LoadCover(currentPlayingMusic);
            }
            UpdatePlayModeIcon();
            isInitializing = false;
        }

        private async Task SavePlayState()
        {
            if (!isInitializing) {
                var playState = await dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
                if (playState == null)
                {
                    playState = new SavePlayState
                    {
                        Id = 1
                    };
                }
                playState.PlayMode = currentPlayMode;
                playState.LastPlayedMusicId = currentPlayingMusic?.Id;
                playState.Volume = volume;
                if (playState.Id == 0)
                {
                    await dbConnection.InsertAsync(playState);
                }
                else
                {
                    await dbConnection.UpdateAsync(playState);
                }
            }            
        }

        private async void PlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    currentPlayMode = PlayMode.ListLoop;
                    break;
                case PlayMode.ListLoop:
                    currentPlayMode = PlayMode.RandomLoop;
                    break;
                case PlayMode.RandomLoop:
                    currentPlayMode = PlayMode.SingleLoop;
                    break;
            }
            await SavePlayState();
            UpdatePlayModeIcon();
        }

        private async void NextMusicButton_Click(object sender, RoutedEventArgs e)
        {
            isManualSelect = true;
            await AutoPlayNextTrack();
            isManualSelect = false;
        }

        private async void LastMusicButton_Click(object sender, RoutedEventArgs e)
        {
            isManualSelect = true;
            await PlayLastTrack();
            isManualSelect = false;
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
            int index = currentPlayingList.IndexOf(currentPlayingMusic);
            if (index > 0)
            {
                await PlayMusic(currentPlayingList[index - 1]);
            }
            else if (index == 0 && currentPlayingList.Count > 1)
            {
                await PlayMusic(currentPlayingList[musicList.Count - 1]);

            }
        }

        private void UpdatePlayModeIcon()
        {
            switch (currentPlayMode)
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
            isInitializing = true;
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            await dbConnection.CreateTableAsync<SavePlayState>();
            _ = LoadPlayState();
            _ = LoadMusic();
            OutputDeviceChange();
            PlayTimeTextBlock.Text = "00:00/00:00";
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (isPlaying)
            {
                if (AppSettings.OutputMode == "WasapiExclusive")
                {
                    isPausing = true;
                    waveOut.Stop();
                    isPlaying = false;
                    progressTimer.Stop();
                }
                else
                {
                    isPausing = true;
                    waveOut.Pause();
                    isPlaying = false;
                    progressTimer.Stop();
                }
                UpdatePlayPauseButtonIcon();
            }
            else
            {
                if (waveOut == null)
                {
                    if (currentPlayingMusic != null)
                    {
                        await PlayMusic(currentPlayingMusic);
                    }
                    else if (musicList != null && musicList.Count > 0)
                    {
                        await PlayMusic(musicList[0]);
                    }
                    else
                    {
                        ShowMessage("没有可播放的音乐");
                        return;
                    }
                }
                else
                {
                    isPausing = false; // Reset pause flag
                    if (AppSettings.OutputMode == "WasapiExclusive")
                    {
                        waveOut.Play();
                        isPlaying = true;
                        progressTimer.Start();
                    }
                    else
                    {
                        waveOut.Play();
                        isPlaying = true;
                        progressTimer.Start();
                    }
                }
                UpdatePlayPauseButtonIcon();
            }
        }

        private void UpdatePlayPauseButtonIcon()
        {
            if (isPlaying)
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769"; // 暂停图标
            }
            else
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE768"; // 播放图标
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (waveOut != null)
            {
                waveOut.Stop();
                isPlaying = false;
                progressTimer.Stop();
                UpdatePlayPauseButtonIcon();
            }
            Reset();
        }

        private async void PlayBarFavouriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentPlayingMusic != null)
            {
                ((FontIcon)PlayBarFavouriteButton.Content).Glyph = !currentPlayingMusic.isFavorite ? "\ueb52" : "\ueb51";
                await AddToFavourite(currentPlayingMusic);
                NotifySubPageUpdateFavouriteState();
            }
        }

        private void NotifySubPageUpdateFavouriteState()
        {
            if (ContentFrame.Content is SongCollectionPage)
            {
                var songCollectionPage = ContentFrame.Content as SongCollectionPage;
                songCollectionPage.UpdateFavouriteMusic(currentPlayingMusic);
            }
            if (ContentFrame.Content is SongListPage)
            {
                var songListPage = ContentFrame.Content as SongListPage;
                songListPage.UpdateFavouriteMusic(currentPlayingMusic);
            }
            if (ContentFrame.Content is FavouritePlayListPage)
            {
                var favouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
                favouritePlayListPage.UpdateFavouriteMusic(currentPlayingMusic);
            }
        }

        private void Reset()
        {
            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (audioFileReader != null)
            {
                audioFileReader.Dispose();
                audioFileReader = null;
            }
            // 停止定时器
            if (progressTimer != null)
            {
                progressTimer.Stop();
            }

            ProgressSlider.Value = 0;
        }
        private async Task<bool> InitializeAudioResources(Music music, TimeSpan currentPos = new TimeSpan())
        {
            try
            {
                if (waveOut != null)
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                    waveOut = null;
                }

                if (audioFileReader != null)
                {
                    audioFileReader.Dispose();
                    audioFileReader = null;
                }
                // 加载新音频
                audioFileReader = new AudioFileReader(music.Path);
                audioFileReader.Volume = volume;
                audioFileReader.CurrentTime = currentPos;
                SelectOutputDevice();
                waveOut.Init(audioFileReader);
                return true;
            }
            catch (Exception ex)
            {
                ShowMessage($"播放失败{ex.Message}");
                System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                Reset();
                return false;
            }
        }

        private async Task LoadCover(Music music)
        {
            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
            {
                AlbumCoverImage.Source = cachedCover;
            }
            else {
                AlbumCoverImage.Source = await GetImageFromMusic(music);
            }            
        }

        public async Task PlayMusic(Music music, TimeSpan currentPos = new TimeSpan(), bool isSettingChanged = false)
        {
            currentPlayingMusic = music;
            MusicTitleTextBlock.Text = music.Title;
            MusicAuthorTextBlock.Text = music.Author;
            MusicInfoTextBlock.Text = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
            ((FontIcon)PlayBarFavouriteButton.Content).Glyph = currentPlayingMusic.isFavorite ? "\ueb52" : "\ueb51";
            HRImage.Source = null;
            if (music.SampleRate >= 48000 && music.BitDepth >= 24)
            {
                var bitmapImage = new BitmapImage(new Uri("ms-appx:///Assets/hr.png"));
                HRImage.Source = bitmapImage;
            }
            var songListPage = ContentFrame.Content as SongListPage;
            var songCollectionPage = ContentFrame.Content as SongCollectionPage;
            var FavouritePlayListPage = ContentFrame.Content as FavouritePlayListPage;
            if (songListPage != null)
            {
                songListPage.UpdateMusicListView();               
            }
            if(songCollectionPage != null)
            {
                songCollectionPage.UpdateMusicListView();
            }
            if (FavouritePlayListPage != null)
            {
                FavouritePlayListPage.UpdateMusicListView();
            }
            await LoadCover(music);
            if (await InitializeAudioResources(music, currentPos))
            {
                try
                {
                    waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                    ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769";
                    if (audioFileReader.TotalTime != null)
                    {
                        ProgressSlider.Maximum = audioFileReader.TotalTime.TotalSeconds;
                    }
                    if (isSettingChanged)
                    {
                        ProgressSlider.Value = currentPos.TotalSeconds;
                    }
                    else
                    {
                        ProgressSlider.Value = 0;
                    }
                    waveOut.Play();
                    isPlaying = true;
                    progressTimer.Start();
                    await SavePlayState();
                }
                catch (Exception ex)
                {
                    ShowMessage($"播放失败{ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
                    Reset();
                }
            }
        }

        private async void WaveOut_PlaybackStopped(object sender, StoppedEventArgs e)
        {
            bool isNaturalEnd = false;

            if (audioFileReader != null && !isPausing && !isManualSelect && !isSettingsChangeStop)
            {
                double currentPositionSeconds = audioFileReader.CurrentTime.TotalSeconds;
                double totalDurationSeconds = audioFileReader.TotalTime.TotalSeconds;
                isNaturalEnd = (totalDurationSeconds - currentPositionSeconds) < 0.5;
            }

            if (isPausing)
            {
                return;
            }

            if (isManualSelect)
            {
                isManualSelect = false;
                return;
            }

            if (isSettingsChangeStop)
            {
                isSettingsChangeStop = false;
                return;
            }

            if (isNaturalEnd)
            {
                await AutoPlayNextTrack();
            }
        }

        private async Task AutoPlayNextTrack()
        {
            switch (currentPlayMode)
            {
                case PlayMode.SingleLoop:
                    await PlayMusic(currentPlayingMusic);
                    break;
                case PlayMode.ListLoop:
                    int currentIndex = currentPlayingList.FindIndex(m => m.Id == currentPlayingMusic.Id);
                    int nextIndex = (currentIndex + 1) % currentPlayingList.Count;
                    await PlayMusic(currentPlayingList[nextIndex]);
                    break;
                case PlayMode.RandomLoop:
                    Random random = new Random();
                    int randomIndex = random.Next(currentPlayingList.Count);
                    await PlayMusic(currentPlayingList[randomIndex]);
                    break;
            }
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
                    if (ContentFrame.Content is AlbumBrowsePage) {
                        var page = ContentFrame.Content as AlbumBrowsePage;
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
                }
            }            
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            volume = (float)e.NewValue / 100;
            if (audioFileReader != null)
            {
                audioFileReader.Volume = volume;
                if (e.NewValue > 66)
                {
                    VolumeSliderIcon.Glyph = "\ue995";
                }
                else if (e.NewValue > 33)
                {
                    VolumeSliderIcon.Glyph = "\ue994";
                }
                else if (e.NewValue > 0)
                {
                    VolumeSliderIcon.Glyph = "\uE993";
                }
                else
                {
                    VolumeSliderIcon.Glyph = "\uE992";
                }                
            }
            SavePlayState();
        }

        private void VolumeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = true;
        }

        private void VolumeSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            isMouseOverVolumeSlider = false;
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

        private void Thumb_DragStarted(object sender, DragStartedEventArgs e)
        {
            isUserDraggingProgressSlider = true;
        }

        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isUserDraggingProgressSlider = false;
            if (audioFileReader != null && isPlaying)
            {
                double newPosition = Math.Max(0, Math.Min(ProgressSlider.Value, audioFileReader.TotalTime.TotalSeconds));
                audioFileReader.CurrentTime = TimeSpan.FromSeconds(newPosition);
            }
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                else
                {
                    var foundChild = FindVisualChild<T>(child);
                    if (foundChild != null)
                    {
                        return foundChild;
                    }
                }
            }
            return null;
        }

        private void ProgressTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try {
                if (audioFileReader != null && isPlaying && !isUserDraggingProgressSlider)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (audioFileReader.CurrentTime != null)
                        {
                            ProgressSlider.Value = audioFileReader.CurrentTime.TotalSeconds;
                            string currentTime = audioFileReader.CurrentTime.ToString(@"mm\:ss");
                            string totalTime = audioFileReader.TotalTime.ToString(@"mm\:ss");
                            PlayTimeTextBlock.Text = $"{currentTime}/{totalTime}";
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"进度条更新失败: {ex.Message}");
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!isUserDraggingProgressSlider && audioFileReader != null && isPlaying)
            {
                double currentPlayPosition = audioFileReader.CurrentTime.TotalSeconds;
                if (Math.Abs(e.NewValue - currentPlayPosition) > 2.0)
                {
                    audioFileReader.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
                }
                else
                {
                    ProgressSlider.Value = currentPlayPosition;
                }
            }
        }
    }
}
