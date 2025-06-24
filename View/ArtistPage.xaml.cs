using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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
    public sealed partial class ArtistPage : Page
    {
        //private MusicBrowsePage parentPage;
        //private ObservableCollection<Music> musicList;
        //private List<Music> _allMusic;
        //private string _lastSearchText = "";
        public ArtistViewModel ViewModel { get; }
        public ArtistPage()
        {
            ViewModel = App.Services.GetRequiredService<ArtistViewModel>();
            ViewModel.SetCurrentPage(this);
            this.InitializeComponent();
            DataContext = this;
            //musicList = new ObservableCollection<Music>();
            //ArtistsGridView.ItemsSource = musicList;
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MusicBrowsePage parentPage)
            {
                ViewModel.SetParentPage(parentPage);                
            }
        }

        //private void RefreshArtist(object? sender, EventArgs e)
        //{
        //    //_currentPage = 0;
        //    InitializeData();
        //}

        public void SortMusicList(string sortOrder)
        {
            ViewModel.SortMusicList(sortOrder);
            //if (_allMusic.Count > 0)
            //{
            //    musicList.Clear();
            //    _allMusic = ToolUtils.SortMusicList("artistCover", sortOrder, _allMusic.ToList());
            //    await LoadMoreArtistAsync(true);
            //}
        }

        //private async void InitializeData()
        //{
        //    try
        //    {
        //        musicList.Clear();
        //        if (parentPage != null)
        //        {
        //            _allMusic = (MusicDatabaseService.GetMusicListFromMem(AppData.searchText)).GroupBy(m => m.Author).Select(g => g.First()).OrderBy(m => m.Author).ToList();
        //            await LoadMoreArtistAsync(true);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
        //    }
        //}

        //private async Task LoadMoreArtistAsync(bool isFirstLoad = false)
        //{

        //    try
        //    {
        //        foreach (var item in _allMusic)
        //        {
        //            musicList.Add(item);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
        //    }
        //}

        private void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.ArtistGridView_ItemClick(sender, e);
            //var gridView = sender as GridView;
            //GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            //if (item != null)
            //{
            //    Music artist = item.Content as Music;
            //    if (parentPage != null)
            //    {
            //        parentPage.LoadArtistMusic(artist.Author);
            //    }
            //}
        }

        private void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ViewModel.Artist_RightTapped(sender, e);
            //var originalSource = e.OriginalSource as FrameworkElement;

            //// 向上遍历查找 GridViewItem
            //GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            //if (clickedItem != null)
            //{
            //    // 从 GridViewItem 获取数据项
            //    var artist = clickedItem.Content as Music;

            //    if (artist != null)
            //    {
            //        // 显示专辑右键菜单
            //        await ContextMenuService.Instance.ShowAlbumContextMenu(
            //            artist,
            //            originalSource,
            //            e.GetPosition(originalSource),
            //            "artist"
            //        );
            //        ContextMenuService.Instance.playingArtistMusic += PlayingArtist;
            //        ContextMenuService.Instance.showTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.ShowTransmission();
            //            }
            //        };
            //        ContextMenuService.Instance.hideTransmission += (s, e) =>
            //        {
            //            if (parentPage != null)
            //            {
            //                parentPage.HideTransmission();
            //            }
            //        };
            //    }
            //}
            //e.Handled = true;
        }

        //private void PlayingArtist(object? sender, Music e)
        //{
            //List<Music> artists = (MusicDatabaseService.GetArtistMusicFromMem(e.Author)).OrderBy(m => m.Album).ToList();
            //if (artists != null && artists.Count > 0)
            //{
            //    if (parentPage != null)
            //    {
            //        parentPage.musicPlaybackService.currentPlayingList = artists;
            //        await parentPage.PlayMusic(artists[0]);
            //    }
            //}
        //}
    }

}
