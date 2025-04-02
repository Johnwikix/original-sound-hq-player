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

        public AlbumBrowsePage()
        {
            try
            {
                this.InitializeComponent();
                InitializeDatabase();
                LoadAlbumsAsync();               
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化专辑页面时出错: {ex.Message}");
            }
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
        }

        private async void LoadAlbumsAsync()
        {
            try
            {
                var musics = await dbConnection.Table<Music>().ToListAsync();
                // 处理没有专辑信息的音乐
                foreach (var music in musics)
                {
                    if (string.IsNullOrEmpty(music.Album))
                    {
                        music.Album = "未知专辑";
                    }
                }
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupedAlbums;
                PopulateAlbumGrid();
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
                var groupedAlbums = musicList.GroupBy(m => m.Album[0]).OrderBy(g => g.Key);
                List<Music> albumsToDisplay = new List<Music>();

                foreach (var group in groupedAlbums)
                {
                    foreach (var album in group)
                    {
                        albumsToDisplay.Add(album);
                    }
                }

                AlbumItemsControl.ItemsSource = albumsToDisplay;

                
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"填充专辑网格失败: {ex.Message}");
            }
        }

        private void Album_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                Debug.WriteLine($"Clicked on album: {music.Album}");
                // 在这里处理专辑点击后的导航或其他操作
            }
        }

    }
}
