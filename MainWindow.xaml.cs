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
using System.Windows.Forms;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
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
        //private SQLiteAsyncConnection dbConnection;
        public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public event EventHandler<List<Music>> MusicListLoaded;
        public event EventHandler<List<Music>> SongCollecionLoaded;
        public event EventHandler<List<Music>> FavourListLoaded;
        public event EventHandler<MMDeviceCollection> SettingLoaded;
        public MainWindow()
        {            
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeApp();
            // 显示 Loading 界面
            //InitializeDatabase();
        }

        private async void InitializeApp()
        {
            try
            {
                await MusicDatabaseService.Initialize();
                await LoadAppState();
                await LoadFoldersAsync();
                await LoadMusicList();
                await RefreshDevice();
                await LoadDeviceState();
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
                case "文件夹选择":
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
                case "音乐列表":
                    ContentFrame.Navigate(typeof(MusicBrowsePage));
                    break;
                default:
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
            }
        }

        //private async void InitializeDatabase()
        //{
        //    try {
        //        var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
        //        dbConnection = new SQLiteAsyncConnection(dbPath);
        //        await dbConnection.CreateTableAsync<Music>();
        //        await dbConnection.CreateTableAsync<Folder>();
        //        await dbConnection.CreateTableAsync<SavePlayState>();
        //        await dbConnection.CreateTableAsync<SaveSettings>();
        //        await LoadState();
        //        await LoadFoldersAsync();
        //        await LoadMusicList();
        //        await RefreshDevice();
        //        await LoadDeviceState();
        //        LoadingGrid.Visibility = Visibility.Collapsed;
        //        NavigationViewControl.Visibility = Visibility.Visible;
        //        switch (AppSettings.DefualtEntry)
        //        {
        //            case "文件夹选择":
        //                ContentFrame.Navigate(typeof(AddFolderPage));
        //                break;
        //            case "音乐列表":
        //                ContentFrame.Navigate(typeof(MusicBrowsePage));
        //                break;
        //            default:
        //                ContentFrame.Navigate(typeof(AddFolderPage));
        //                break;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"错误: {ex.Message}");
        //    }
            
        //}
        public async Task LoadFoldersAsync()
        {
            var folderList = await MusicDatabaseService.GetFoldersAsync();
            FoldersLoaded?.Invoke(this, folderList);
            //try
            //{
            //    var folderList = await dbConnection.Table<Folder>().ToListAsync();
            //    // 触发事件通知子页面更新
            //    FoldersLoaded?.Invoke(this, folderList);
            //}
            //catch (SQLiteException ex)
            //{
            //    System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            //}
        }

        //private async Task LoadAlbumCover(List<Music> musics) {
            //try
            //{
            //    var groupedAlbums = musics.GroupBy(m => m.Album)
            //                                 .Select(g => g.First())
            //                                 .ToList();
            //    foreach (var album in groupedAlbums)
            //    {
            //        if (AppData.albumCoverCache.TryGetValue(album.Album, out var cachedCover))
            //        {
            //            album.Cover = cachedCover;
            //        }
            //        else
            //        {
            //            BitmapImage cover = await GetAlbumCover(album,musics);
            //            album.Cover = cover;
            //            AppData.albumCoverCache[album.Album] = cover;
            //        }
            //    }
            //}
            //catch (Exception ex)
            //{
            //    Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            //}
        //}        

        public async Task LoadMusicList(string search = null)
        {
            var musicList = await MusicDatabaseService.GetMusicListAsync(search);
            await AlbumCoverService.LoadAlbumCoversAsync(musicList);
            MusicListLoaded?.Invoke(this, musicList);
            //var query = dbConnection.Table<Music>();
            //if (!string.IsNullOrEmpty(search))
            //{
            //    query = query.Where(m =>
            //        m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //        m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
            //        m.Album != null && m.Album.ToLower().Contains(search.ToLower())
            //    );
            //}
            //var musicList = await query.OrderBy(m => m.Title).ToListAsync();
            //await LoadAlbumCover(musicList);
            //MusicListLoaded?.Invoke(this, musicList);           
        }

        public async Task LoadFavourMusicList(string search = null)
        {
            var musicList = await MusicDatabaseService.GetFavoriteMusicAsync(search);
            FavourListLoaded?.Invoke(this, musicList);
            //var query = dbConnection.Table<Music>();
            //query = query.Where(m =>
            //         m.isFavorite == true &&
            //         (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //         m.Author != null && m.Author.ToLower().Contains(search.ToLower()) ||
            //         m.Album != null && m.Album.ToLower().Contains(search.ToLower()))
            //     );
            //var musicList = await query.OrderByDescending(m => m.Order).ToListAsync();
            //FavourListLoaded?.Invoke(this, musicList);
        }

        public async Task LoadArtistMusic(string artist, string search = null)
        {
            var musicList = await MusicDatabaseService.GetArtistMusicAsync(artist, search);
            SongCollecionLoaded?.Invoke(this, musicList);
            //var query = dbConnection.Table<Music>();
            //if (!string.IsNullOrEmpty(artist))
            //{
            //    query = query.Where(m =>
            //       (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //       m.Album != null && m.Author.ToLower().Contains(search.ToLower())) &&
            //       m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
            //    );
            //}
            //var musicList = await query.OrderBy(m => m.Album).ToListAsync();
            //SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadFolderMusic(string folder, string search = null)
        {
            var musicList = await MusicDatabaseService.GetFolderMusicAsync(folder, search);
            SongCollecionLoaded?.Invoke(this, musicList);
            //var query = dbConnection.Table<Music>();
            //if (!string.IsNullOrEmpty(folder))
            //{
            //    query = query.Where(m =>
            //       (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //       m.Album != null && m.Author.ToLower().Contains(search.ToLower()) ||
            //       m.Author != null && m.Author.ToLower().Contains(search.ToLower())) &&
            //       m.LastLevelFolderPath != null && m.LastLevelFolderPath.ToLower().Equals(folder.ToLower())
            //    );
            //}
            //var musicList = await query.OrderBy(m => m.LastLevelFolderPath).ToListAsync();
            //SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadAlbumMusic(string album,string search = null) {
            var musicList = await MusicDatabaseService.GetAlbumMusicAsync(album, search);
            SongCollecionLoaded?.Invoke(this, musicList);
            //var query = dbConnection.Table<Music>();
            //if (!string.IsNullOrEmpty(album))
            //{
            //    query = query.Where(m =>
            //        (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
            //        m.Author != null && m.Author.ToLower().Contains(search.ToLower())) &&
            //        m.Album != null && m.Album.ToLower().Equals(album.ToLower())
            //    );
            //}
            //var musicList = await query.OrderBy(m => m.TrackNumber).ToListAsync();
            //SongCollecionLoaded?.Invoke(this, musicList);
        }


        //private async Task LoadState()
        //{
        //    DateTime dateTime = DateTime.Now;
        //    var playState = await dbConnection.Table<SavePlayState>().FirstOrDefaultAsync();
        //    if (playState == null)
        //    {
        //        // 如果没有记录，默认设置为列表循环
        //        playState = new SavePlayState
        //        {
        //            PlayMode = PlayMode.ListLoop,
        //            Volume = 0.5f,
        //            LastPlayedMusicId = null
        //        };
        //        await dbConnection.InsertAsync(playState);
        //    }
        //    AppData.PlayMode = playState.PlayMode;
        //    AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
        //    AppData.Volume = playState.Volume;
        //    System.Diagnostics.Debug.WriteLine($"LoadState 耗时: {(DateTime.Now - dateTime).TotalMilliseconds}ms");
        //}
        public async Task RefreshDevice() {
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
        public async Task LoadDeviceState() {
            AppSettings.outputDeviceList.Clear();
            var settings = await MusicDatabaseService.GetSettingsAsync();
            if (settings != null)
            {
                AppSettings.DefualtEntry = settings.DefualtEntry;
                AppSettings.DefualtPlayList = settings.DefualtPlayList;
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                AppSettings.DeviceName = settings.DeviceFriendlyName;
            }
            //AppSettings.outputDeviceList.Clear();
            //var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();            
            //if (settings != null)
            //{
            //    AppSettings.DefualtEntry = settings.DefualtEntry;
            //    AppSettings.DefualtPlayList = settings.DefualtPlayList;
            //    AppSettings.OutputMode = settings.OutputMode;
            //    AppSettings.Latency = settings.Latency;                        
            //    AppSettings.DeviceName = settings.DeviceFriendlyName;                        
            //}            
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
