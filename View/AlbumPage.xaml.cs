using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumPage : Page
    {
        private List<Music> musicList;
        private readonly object _updateLock = new object();
        private MusicBrowsePage parentPage;
        public AlbumPage()
        {
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentAlbumName = null;
                parentPage.DisableBackButton();
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

        private async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var button = sender as Button;
            var album = button.DataContext;

            ContextMenuService.Instance.SetAlbumPage(this);
            // 显示专辑右键菜单
            await ContextMenuService.Instance.ShowAlbumContextMenu(
                album,
                button,
                e.GetPosition(button),
                "album"
            );

            e.Handled = true;
        }

        public async void OnAlbumDetailChanged(object sender, EventArgs e)
        {
            var musicList = await MusicDatabaseService.GetMusicListAsync(parentPage.searchText);
            _ = AlbumCoverService.LoadAlbumCoversAsync(musicList);
            LoadAlbumsAsync(musicList);            
        }

    }
}

