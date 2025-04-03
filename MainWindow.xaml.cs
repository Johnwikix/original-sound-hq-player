using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private SQLiteAsyncConnection dbConnection;
        public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public event EventHandler<List<Music>> MusicListLoaded;
        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeDatabase();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            await dbConnection.CreateTableAsync<Folder>();
            await dbConnection.CreateTableAsync<SavePlayState>();
            await dbConnection.CreateTableAsync<SaveSettings>();
            LoadState();
            LoadMusicList();
            LoadFoldersAsync();

        }
        public async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await dbConnection.Table<Folder>().ToListAsync();
                // 触发事件通知子页面更新
                FoldersLoaded?.Invoke(this, folderList);
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            }
        }

        public async Task LoadMusicList(string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(search))
            {
                string searchPattern = $"%{search}%";
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            MusicListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadArtistMusic(string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(search))
            {
                string searchPattern = $"%{search}%";
                query = query.Where(m =>
                   m.Author != null && m.Author.ToLower().Contains(search.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            MusicListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadAlbumMusic(string search = null) {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(search))
            {
                string searchPattern = $"%{search}%";
                query = query.Where(m =>
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            MusicListLoaded?.Invoke(this, musicList);
        }


        private async Task LoadState()
        {
            var playState = await dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
            if (playState == null)
            {
                // 如果没有记录，默认设置为列表循环
                playState = new SavePlayState
                {
                    PlayMode = PlayMode.ListLoop,
                    Volume = 0.5f,
                    LastPlayedMusicId = null
                };
                await dbConnection.InsertAsync(playState);
            }
            AppData.PlayMode = playState.PlayMode;
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppData.Volume = playState.Volume;
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings != null)
            {
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    if (device.FriendlyName == settings.DeviceFriendlyName)
                    {
                        AppSettings.OutputDevice.mMDevice = device;
                        AppSettings.DeviceName = device.FriendlyName;
                        break;
                    }
                }
                foreach (var device in devices)
                {
                    AppSettings.outputDeviceList.Add(device.FriendlyName);
                }
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 移除事件处理程序，避免重复触发
            this.Activated -= MainWindow_Activated;
            // 初始导航到 AddFolder 页面
            NavigateToPage(typeof(AddFolderPage));
        }
        private void NavigateToPage(Type pageType)
        {
            ContentFrame.Navigate(pageType);
        }


        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                NavigateToPage(typeof(SettingsPage));
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        NavigateToPage(typeof(AddFolderPage));
                        break;
                    case "MusicBrowse":
                        NavigateToPage(typeof(MusicBrowsePage));
                        break;
                }
            }
        }
    }
}
