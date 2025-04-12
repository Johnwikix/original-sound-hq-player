using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
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

            // 显示专辑右键菜单
            await ContextMenuService.Instance.ShowAlbumContextMenu(
                album,
                button,
                e.GetPosition(button),
                "album"
            );

            e.Handled = true;
        }

        //private async void AddToFavourite_Click(object sender, RoutedEventArgs e)
        //{
        //    var menuItem = sender as MenuFlyoutItem;
        //    var music = menuItem?.DataContext as Music;
        //    if (music != null)
        //    {
        //        List<Music> musicList = await MusicDatabaseService.FindMusicListByAlbum(music.Album);
        //        if (musicList != null)
        //        {
        //            _ = MusicDatabaseService.AddMusicListToFavour(musicList);
        //        }
        //    }
        //}

        //private async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        //{
        //    Button button = sender as Button;
        //    var albumItem = button.DataContext;

        //    // 创建菜单
        //    MenuFlyout flyout = new MenuFlyout();

        //    // 添加"添加到最爱"菜单项
        //    MenuFlyoutItem favoriteItem = new MenuFlyoutItem
        //    {
        //        Text = "添加到最爱",
        //        DataContext = albumItem
        //    };
        //    favoriteItem.Click += AddToFavourite_Click;
        //    flyout.Items.Add(favoriteItem);

        //    // 创建"添加到播放列表"子菜单
        //    MenuFlyoutSubItem playlistSubItem = new MenuFlyoutSubItem
        //    {
        //        Text = "添加到播放列表"
        //    };

        //    // 获取所有播放列表
        //    List<PlayList> playlists = await MusicDatabaseService.GetPlayListAsync();
        //    foreach (PlayList playlist in playlists)
        //    {
        //        MenuFlyoutItem menuItem = new MenuFlyoutItem
        //        {
        //            Text = playlist.Name,
        //            DataContext = new { Album = albumItem, Playlist = playlist }
        //        };
        //        menuItem.Click += AddToPlaylistMenuItem_Click;
        //        playlistSubItem.Items.Add(menuItem);
        //    }

        //    flyout.Items.Add(playlistSubItem);

        //    // 显示菜单
        //    flyout.ShowAt(button, e.GetPosition(button));
        //    e.Handled = true;
        //}

        //private void AddToPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        //{
        //    var menuItem = sender as MenuFlyoutItem;
        //    var data = menuItem.DataContext as dynamic;

        //    // 现在你可以访问数据中的Album和Playlist属性
        //    var album = data.Album;
        //    var playlist = data.Playlist;

        //    // 处理添加到播放列表的逻辑
        //}

    }
}

