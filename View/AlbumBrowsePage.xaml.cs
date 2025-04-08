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
using System.Collections.ObjectModel;
using WinUIMusicPlayer.Utils;

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
                parentPage.currentAlbumName = null;
                InitializeDatabase();
            }
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (musicList.Count > 0)
            {
                musicList = ToolUtils.SortMusicList("albumCover", order, musicList.ToList());
            }
            AlbumItemsControl.ItemsSource = musicList;
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

        public void LoadAlbumsAsync(List<Music> musics)
        {
            try
            {
                var groupedAlbums = musics.GroupBy(m => m.Album)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupedAlbums.OrderBy(m => m.Album).ToList();
                SortMusicList("DefaultOrder");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }           

        private void Album_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(music.Album);
                }
            }
        }

    }
}
