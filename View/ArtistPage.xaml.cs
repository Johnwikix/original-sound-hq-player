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
    public sealed partial class ArtistPage : Page
    {
        private MusicBrowsePage parentPage;
        private List<Music> musicList;
        public ArtistPage()
        {
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentArtistName = null;
                parentPage.DisableBackButton();
                InitializeData();
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
                musicList = ToolUtils.SortMusicList("artistCover", order, musicList.ToList());
            }
            ArtistsItemsControl.ItemsSource = musicList;
        }

        private async void InitializeData()
        {
            try
            {
                if (parentPage != null)
                {
                    await parentPage.LoadMusic();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
            }
        }

        public void LoadArtists(List<Music> musics)
        {
            try
            {
                var groupArtists = musics.GroupBy(m => m.Author)
                                             .Select(g => g.First())
                                             .ToList();
                musicList = groupArtists;
                SortMusicList("DefaultOrder");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }

        private void Artist_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is Music music)
            {
                Debug.WriteLine($"Clicked on artist: {music.Author}");
                if (parentPage != null)
                {
                    parentPage.LoadArtistMusic(music.Author);
                }
            }
        }

        private async void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var button = sender as Button;
            var album = button.DataContext;

            // 显示专辑右键菜单
            await ContextMenuService.Instance.ShowAlbumContextMenu(
                album,
                button,
                e.GetPosition(button),
                "artist"
            );

            e.Handled = true;
        }

        //private async void AddToFavourite_Click(object sender, RoutedEventArgs e)
        //{
        //    var menuItem = sender as MenuFlyoutItem;
        //    var music = menuItem?.DataContext as Music;
        //    Debug.WriteLine($"专辑名称: {music.Album}, 艺术家: {music.Author}");
        //    if (music != null)
        //    {
        //        List<Music> musicList = await MusicDatabaseService.FindMusicListByArtist(music.Author);
        //        if (musicList != null)
        //        {
        //            _=MusicDatabaseService.AddMusicListToFavour(musicList);
        //        }
        //    }
        //}
    }

}
