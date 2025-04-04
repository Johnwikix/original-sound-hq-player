using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SQLite;
using System.Diagnostics;
using WinUIMusicPlayer.Model;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumBrowsePage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private List<Music> musicList;
        private readonly object _updateLock = new object();
        private MusicBrowsePage parentPage;        

        public AlbumBrowsePage()
        {
            try
            {
                this.InitializeComponent();                
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化专辑页面时出错: {ex.Message}");
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                InitializeDatabase();
            }
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            if (parentPage != null)
            {
                await parentPage.LoadMusic();
            }
        }

        public async void LoadAlbumsAsync(List<Music> musics)
        {
            try
            {
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupedAlbums.OrderBy(m => m.Album).ToList();
                PopulateAlbumGrid();
                await UpdateAlbumCoversAsync(musicList);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }        

        private void PopulateAlbumGrid()
        {
            try
            {
                AlbumItemsControl.ItemsSource = musicList;                
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"填充专辑网格失败: {ex.Message}");
            }
        }

        private async Task UpdateAlbumCoversAsync(List<Music> albums)
        {
            foreach (var album in albums)
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
            AlbumItemsControl.ItemsSource = null;
            AlbumItemsControl.ItemsSource = albums;
        }

        private async Task<BitmapImage> GetAlbumCover(Music album)
        {
            BitmapImage newCover = album.Cover;
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
                                    bitmapImage.DecodePixelWidth = 125;
                                    bitmapImage.DecodePixelHeight = 125;
                                    await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                                    newCover = bitmapImage;                                    
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                    }
                }                
            }
            return newCover;
        } 

        private void Album_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                Debug.WriteLine($"Clicked on album: {music.Album}");
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(music.Album);
                }
            }
        }

    }
}
