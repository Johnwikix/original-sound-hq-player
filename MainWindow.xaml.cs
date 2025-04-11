using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public event EventHandler<List<Music>> MusicListLoaded;
        public event EventHandler<List<Music>> SongCollecionLoaded;
        public event EventHandler<List<Music>> FavourListLoaded;
        public event EventHandler<MMDeviceCollection> SettingLoaded;
        public event EventHandler<List<PlayList>> PlayListLoaded;
        public event EventHandler<List<Music>> PlayMusicListLoaded;
        private SystemMediaTransportControls systemMediaControls;
        private MusicPlaybackService playbackService = new MusicPlaybackService();
        private MediaPlayer mediaPlayer;
        private bool isPlaying;
        internal interface IWindowNative
        {
            IntPtr WindowHandle { get; }
        }
        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeApp();
            InitializeSystemMediaTransportControls();
        }

        private void InitializeSystemMediaTransportControls()
        {
            try
            {
                // 获取当前窗口的句柄
                //var windowNative = this.As<IWindowNative>();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                mediaPlayer = new MediaPlayer();
                // 获取系统媒体传输控件
                systemMediaControls = mediaPlayer.SystemMediaTransportControls;
                // 确保控件已初始化
                if (systemMediaControls != null)
                {
                    // 启用控件
                    systemMediaControls.IsPlayEnabled = true;
                    systemMediaControls.IsPauseEnabled = true;
                    systemMediaControls.IsNextEnabled = true;
                    systemMediaControls.IsPreviousEnabled = true;

                    // 注册按钮事件
                    systemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;

                    // 更新初始状态
                    UpdateSystemMediaControlsState();
                }
            }
            catch (Exception ex)
            {
                // 处理并记录错误
                System.Diagnostics.Debug.WriteLine($"初始化 SMTC 失败: {ex.Message}");
            }
        }

        private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            // 这里我们需要将 SMTC 按钮事件转发给你现有的 NAudio 播放逻辑
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    playbackService.PlayButton();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    //playbackService.PlayButton();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    //playbackService.AutoPlayNextTrack();
                    break;
                case SystemMediaTransportControlsButton.Previous:
              
                    break;
            }
        }

        private void UpdateSystemMediaControlsState()
        {
            // 根据实际播放状态更新 SMTC
            systemMediaControls.PlaybackStatus = isPlaying ?
                MediaPlaybackStatus.Playing :
                MediaPlaybackStatus.Paused;
        }

        private async Task UpdateMediaInfo(string title, string artist, string album, string albumArtPath = null)
        {
            var updater = systemMediaControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;

            // 清除所有属性
            updater.ClearAll();

            // 设置音乐属性
            var musicProperties = updater.MusicProperties;
            musicProperties.Title = title;
            musicProperties.Artist = artist;
            musicProperties.AlbumTitle = album;

            // 如果有专辑封面，设置
            if (!string.IsNullOrEmpty(albumArtPath))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(albumArtPath);
                    updater.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"设置专辑封面失败: {ex.Message}");
                }
            }

            // 应用更改
            updater.Update();
        }

        private async void InitializeApp()
        {
            try
            {
                await MusicDatabaseService.Initialize();
                var tasks = new Task[] {
                        LoadAppState(),
                        LoadFoldersAsync(),
                        LoadMusicList(),
                        RefreshDevice(),
                        LoadDeviceState(),
                        LoadPlayList()
                };
                await Task.WhenAll(tasks);
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                NavigateToDefaultPage();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化错误: {ex.Message}");
            }
        }

        private void NavigateToDefaultPage()
        {
            switch (AppSettings.DefualtEntry)
            {
                case "folderPicker":
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
                case "musicList":
                    ContentFrame.Navigate(typeof(MusicBrowsePage));
                    break;
                default:
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
            }
        }
        public async Task LoadFoldersAsync()
        {
            var folderList = await MusicDatabaseService.GetFoldersAsync();
            FoldersLoaded?.Invoke(this, folderList);
        }
        public async Task LoadPlayList()
        {
            var playList = await MusicDatabaseService.GetPlayListAsync();
            PlayListLoaded?.Invoke(this, playList);
        }

        public async Task LoadPlayListMusic(int playListId, string search = null)
        {
            var musicList = await MusicDatabaseService.GetMusicByPlayListId(playListId, search);
            PlayMusicListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadMusicList(string search = null)
        {
            var musicList = await MusicDatabaseService.GetMusicListAsync(search);
            await AlbumCoverService.LoadAlbumCoversAsync(musicList);
            MusicListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadFavourMusicList(string search = null)
        {
            var musicList = await MusicDatabaseService.GetFavoriteMusicAsync(search);
            FavourListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadArtistMusic(string artist, string search = null)
        {
            var musicList = await MusicDatabaseService.GetArtistMusicAsync(artist, search);
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadFolderMusic(string folder, string search = null)
        {
            var musicList = await MusicDatabaseService.GetFolderMusicAsync(folder, search);
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadAlbumMusic(string album, string search = null)
        {
            var musicList = await MusicDatabaseService.GetAlbumMusicAsync(album, search);
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task RefreshDevice()
        {
            await Task.Run(() =>
            {
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                AppSettings.outputDeviceList.Clear();
                foreach (var device in devices)
                {
                    AppSettings.outputDeviceList.Add(device.FriendlyName);
                }
                // 回到 UI 线程触发事件
                if (SettingLoaded != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SettingLoaded?.Invoke(this, devices);
                    });
                }
            });
        }

        private async Task LoadAppState()
        {
            var playState = await MusicDatabaseService.GetPlayStateAsync();
            AppData.PlayMode = playState.PlayMode;
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppData.Volume = playState.Volume;
        }
        public async Task LoadDeviceState()
        {
            await MusicDatabaseService.GetSettingsAsync();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 移除事件处理程序，避免重复触发
            this.Activated -= MainWindow_Activated;
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(SettingsPage), this);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        ContentFrame.Navigate(typeof(AddFolderPage));
                        break;
                    case "MusicBrowse":
                        ContentFrame.Navigate(typeof(MusicBrowsePage));
                        break;
                }
            }
        }
    }
}
