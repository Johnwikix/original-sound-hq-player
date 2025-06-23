using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AlbumPage : Page
    {
        private MusicBrowsePage parentPage;
        public AlbumViewModel ViewModel { get; }
        public AlbumPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<AlbumViewModel>();
            DataContext = this;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            DateTime startTime = DateTime.Now;
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                this.parentPage = parentPage;
                parentPage.currentAlbumName = null;
                parentPage.pageType = null;
                parentPage.DisableBackButton();
                parentPage.refreshPage += RefreshAlbum;                
                parentPage.refreshUsbDeviceMusicList +=
                    (s, e) =>
                    {
                        ToolUtils.RefreshIcon(ViewModel.MusicList, "album");
                    };
                parentPage.clearUsbDeviceMusicList +=
                    (s, e) =>
                    {
                        ToolUtils.RefreshIcon(ViewModel.MusicList, "album");
                    };
                ViewModel.Entance();
            }            
        }
       

        private async void RefreshAlbum(object? sender, EventArgs e)
        {
            ViewModel.InitializeData();
        }


        public async void SortMusicList(string sortOrder = "DefaultOrder")
        {
            await ViewModel.SortMusicList(sortOrder);           
        }
        

        private async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;

            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem != null)
            {
                // 从 GridViewItem 获取数据项
                var album = clickedItem.Content as Music;

                if (album != null)
                {
                    ContextMenuService.Instance.SetAlbumPage(this);
                    // 显示专辑右键菜单
                    await ContextMenuService.Instance.ShowAlbumContextMenu(
                        album,
                        originalSource,
                        e.GetPosition(originalSource),
                        "album"
                    );
                    ContextMenuService.playingAlbumMusic += PlayingAlbum;
                    ContextMenuService.showTransmission += (s, e) =>
                    {
                        if (parentPage != null)
                        {
                            parentPage.ShowTransmission();
                        }
                    };
                    ContextMenuService.hideTransmission += (s, e) =>
                    {
                        if (parentPage != null)
                        {
                            parentPage.HideTransmission();
                        }
                    };
                }
            }
            e.Handled = true;
        }

        private async void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = MusicDatabaseService.GetAlbumMusicFromMem(e.Album).OrderBy(m => m.Album).ToList();
            if (albums != null && albums.Count > 0)
            {
                if (parentPage != null)
                {
                    parentPage.musicPlaybackService.currentPlayingList = albums;
                    await parentPage.PlayMusic(albums[0]);
                }
            }
        }

        public async void OnAlbumDetailChanged(object sender, Music cover)
        {
            var musicToUpdate = ViewModel.MusicList
                .FirstOrDefault(music => music.Album == cover.Album);
            if (musicToUpdate != null)
            {
                musicToUpdate.Cover = cover.Cover;
                musicToUpdate.Year = cover.Year;
                musicToUpdate.Album = cover.Album;
            }
            //foreach (var music in ViewModel.MusicList)
            //{
            //    if (music.Album == cover.Album)
            //    {
            //        music.Cover = cover.Cover;
            //        music.Year = cover.Year;
            //        music.Album = cover.Album;
            //        break;
            //    }
            //}
        }

        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music album = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadAlbumMusic(album.Album);
                }
            }
        }
    }
}

