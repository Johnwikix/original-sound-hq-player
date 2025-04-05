using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;
using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public event EventHandler<List<Music>> SongCollecionLoaded;
        public MainWindow()
        {            
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            // 显示 Loading 界面
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
            await LoadState();
            await LoadFoldersAsync();
            await LoadMusicList();
            await LoadDeviceState();
            LoadingGrid.Visibility = Visibility.Collapsed;
            NavigationViewControl.Visibility = Visibility.Visible;
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

        private async Task LoadAlbumCover(List<Music> musics) {
            try
            {
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                             .Select(g => g.First())
                                             .ToList();
                foreach (var album in groupedAlbums)
                {
                    if (AppData.albumCoverCache.TryGetValue(album.Album, out var cachedCover))
                    {
                        album.Cover = cachedCover;
                    }
                    else
                    {
                        BitmapImage cover = await GetAlbumCover(album,musics);
                        album.Cover = cover;
                        AppData.albumCoverCache[album.Album] = cover;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }        

        public async Task LoadMusicList(string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(m =>
                    m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
                    m.Album != null && m.Album.ToLower().Contains(search.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            await LoadAlbumCover(musicList);
            MusicListLoaded?.Invoke(this, musicList);           
        }

        public async Task LoadArtistMusic(string artist, string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(artist))
            {
                query = query.Where(m =>
                   (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                   m.Album != null && m.Author.ToLower().Contains(search.ToLower())) &&
                   m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Album).ToListAsync();
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadFolderMusic(string folder, string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(folder))
            {
                query = query.Where(m =>
                   (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                   m.Album != null && m.Author.ToLower().Contains(search.ToLower()) ||
                   m.Author != null && m.Author.ToLower().Contains(search.ToLower())) &&
                   m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.LastLevelFolderPath).ToListAsync();
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadAlbumMusic(string album,string search = null) {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(album))
            {
                query = query.Where(m =>
                    (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                    m.Author != null && m.Author.ToLower().Contains(search.ToLower())) &&
                    m.Album != null && m.Album.ToLower().Equals(album.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.TrackNumber).ToListAsync();
            SongCollecionLoaded?.Invoke(this, musicList);
        }


        private async Task LoadState()
        {
            DateTime dateTime = DateTime.Now;
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
            System.Diagnostics.Debug.WriteLine($"LoadState 耗时: {(DateTime.Now - dateTime).TotalMilliseconds}ms");
        }
        public async Task LoadDeviceState() {
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings != null)
            {
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                AppSettings.DeviceName = settings.DeviceFriendlyName;   
                AppSettings.outputDeviceList.Clear();
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
            ContentFrame.Navigate(typeof(AddFolderPage));
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(SettingsPage),this);
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
