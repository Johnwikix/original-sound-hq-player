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
                        BitmapImage cover = await GetAlbumCover(album);
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

        private async Task<BitmapImage> GetAlbumCover(Music album)
        {
            BitmapImage newCover = album.Cover;
            bool isCoverFound = false;
            if (album.Album != "未知专辑")
            {
                var albumSongs = await dbConnection.Table<Music>().Where(m => m.Album == album.Album).ToListAsync();
                foreach (var song in albumSongs)
                {
                    try
                    {
                        using (var file = TagLib.File.Create(song.Path))
                        {
                            if (file.Tag.Pictures.Length > 0)
                            {
                                var picture = file.Tag.Pictures[0];
                                using (var ms = new MemoryStream(picture.Data.Data))
                                {
                                    var bitmapImage = new BitmapImage();
                                    bitmapImage.ImageOpened += (sender, args) =>
                                    {
                                        double originalWidth = bitmapImage.PixelWidth;
                                        double originalHeight = bitmapImage.PixelHeight;
                                        double aspectRatio = originalWidth / originalHeight;
                                        int maxSize = 125;
                                        int newWidth, newHeight;
                                        if (originalWidth > originalHeight)
                                        {
                                            newWidth = maxSize;
                                            newHeight = (int)(maxSize / aspectRatio);
                                        }
                                        else
                                        {
                                            newHeight = maxSize;
                                            newWidth = (int)(maxSize * aspectRatio);
                                        }
                                        bitmapImage.DecodePixelWidth = newWidth;
                                        bitmapImage.DecodePixelHeight = newHeight;
                                    };
                                    await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                                    newCover = bitmapImage;
                                    isCoverFound = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                    }
                    if (!isCoverFound)
                    {
                        var uri = new Uri("ms-appx:///Assets/Album.png");
                        var bitmapImage = new BitmapImage(uri);
                        bitmapImage.DecodePixelWidth = 125;
                        bitmapImage.DecodePixelHeight = 125;
                        newCover = bitmapImage;
                    }
                }
            }
            else {
                var uri = new Uri("ms-appx:///Assets/Album.png");
                var bitmapImage = new BitmapImage(uri);
                bitmapImage.DecodePixelWidth = 125;
                bitmapImage.DecodePixelHeight = 125;
                newCover = bitmapImage;
            }
            return newCover;
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
            _ = LoadAlbumCover(musicList);
        }

        public async Task LoadArtistMusic(string artist, string search = null)
        {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(artist))
            {
                string searchPattern = $"%{artist}%";
                query = query.Where(m =>
                   (m.Title != null && m.Title.ToLower().Contains(search.ToLower()) ||
                   m.Album != null && m.Author.ToLower().Contains(search.ToLower())) &&
                   m.Author != null && m.Author.ToLower().Equals(artist.ToLower())
                );
            }
            var musicList = await query.OrderBy(m => m.Album).ToListAsync();
            SongCollecionLoaded?.Invoke(this, musicList);
        }

        public async Task LoadAlbumMusic(string album,string search = null) {
            var query = dbConnection.Table<Music>();
            if (!string.IsNullOrEmpty(album))
            {
                string searchPattern = $"%{album}%";
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
        private async Task LoadDeviceState() {
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            if (settings != null)
            {
                AppSettings.OutputMode = settings.OutputMode;
                AppSettings.Latency = settings.Latency;
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                AppSettings.DeviceName = settings.DeviceFriendlyName;
                //foreach (var device in devices)
                //{
                //    if (device.FriendlyName == settings.DeviceFriendlyName)
                //    {
                //        AppSettings.OutputDevice.mMDevice = device;
                //        AppSettings.DeviceName = device.FriendlyName;
                //        break;
                //    }
                //}
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
