using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        public event EventHandler SettingLoaded;
        public event EventHandler<List<PlayList>> PlayListLoaded;
        public event EventHandler<List<Music>> PlayMusicListLoaded;
        public event EventHandler WindowClosed;

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeApp();
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow closed.");
            WindowClosed?.Invoke(this, EventArgs.Empty);
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
            foreach (var item in NavigationViewControl.MenuItems)
            {
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == AppSettings.DefualtEntry)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (AppSettings.DefualtEntry)
            {
                case "AddFolder":
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
                case "MusicBrowse":
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
            _ = AlbumCoverService.LoadAlbumCoversInCacheAsync(musicList);
            MusicListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadFavourMusicList(string search = null)
        {
            var musicList = await MusicDatabaseService.GetFavoriteMusicAsync(search);
            FavourListLoaded?.Invoke(this, musicList);
        }

        public async Task RefreshDevice()
        {
            try {
                if (!AppSettings.isDsd)
                {
                    await Task.Run(() =>
                    {
                        using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                        {
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
                                    SettingLoaded?.Invoke(this, EventArgs.Empty);
                                });
                            }
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    });
                }
                else
                {
                    using (var csCoreEnumerator = new CSCore.CoreAudioAPI.MMDeviceEnumerator())
                    {
                        using (var devices = csCoreEnumerator.EnumAudioEndpoints(CSCore.CoreAudioAPI.DataFlow.Render, CSCore.CoreAudioAPI.DeviceState.Active))
                        {
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
                                    SettingLoaded?.Invoke(this, EventArgs.Empty);
                                });
                            }
                        }
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
            }
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
