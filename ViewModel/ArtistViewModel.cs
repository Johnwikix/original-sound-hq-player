using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class ArtistViewModel : ObservableObject
    {
        private ObservableCollection<Music> _musicList = [];
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }
        private CollectionViewSource _groupedMusicViewSource;
        public CollectionViewSource GroupedMusicViewSource
        {
            get => _groupedMusicViewSource;
            set => SetProperty(ref _groupedMusicViewSource, value);
        }
        private List<MusicGroup> groupedByFirstLetter = [];
        //private List<Music>? _allMusic;
        private string _lastSearchText = "";
        private MusicBrowsePage? parentPage;
        private MusicBrowseViewModel? _musicBrowseViewModel;
        private ArtistPage? currentPage;
        private ContextMenuService _contextMenuService;

        public ArtistViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel? musicBrowseViewModel)
        {
            parentPage = parent;
            GroupedMusicViewSource = new CollectionViewSource
            {
                IsSourceGrouped = true
            };
            parentPage.refreshPage += RefreshArtist;
            //parentPage.refreshUsbDeviceMusicList +=
            //    (s, e) =>
            //    {
            //        ToolUtils.RefreshIcon(MusicList, "album");
            //    };
            //parentPage.clearUsbDeviceMusicList +=
            //    (s, e) =>
            //    {
            //        ToolUtils.RefreshIcon(MusicList, "album");
            //    };
            _contextMenuService = contextMenuService;
            _contextMenuService.showTransmission += (s, e) =>
            {
                if (parentPage is not null)
                {
                    parentPage.ShowTransmission();
                }
            };
            _contextMenuService.hideTransmission += (s, e) =>
            {
                if (parentPage is not null)
                {
                    parentPage.HideTransmission();
                }
            };
            _musicBrowseViewModel = musicBrowseViewModel;
        }

        public void UpdateUsbIcon()
        {
            ToolUtils.RefreshIcon(MusicList, "artist");
        }

        public void SetCurrentPage(ArtistPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            parentPage.ViewModel.CurrentArtist = null;
            parentPage.ViewModel.PageType = "artistBrowse";
            parentPage.DisableBackButton();
            if (_lastSearchText != AppData.searchText || MusicList is null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeData();
            }
            else
            {
                Debug.WriteLine("搜索条件未变更，保留当前视图状态");
            }
            ToolUtils.RefreshIcon(MusicList, "artist");
        }

        private void RefreshArtist(object? sender, bool e)
        {
            InitializeData();
        }

        private void InitializeData()
        {
            try
            {
                //_allMusic = (MusicDatabaseService.GetMusicListFromMem(AppData.searchText)).GroupBy(m => m.Author).Select(g => g.First()).OrderBy(m => m.Author).ToList();
                MusicList.Clear();
                var query = (MusicDatabaseService.GetMusicListFromMem(AppData.searchText)).GroupBy(m => m.Author).Select(g => g.First()).OrderBy(m => m.Author);
                foreach (var music in query)
                {
                    MusicList.Add(music);
                }
                LoadMoreArtistAsync(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化艺术家页面时出错: {ex.Message}");
            }
        }

        private void LoadMoreArtistAsync(bool isFirstLoad = false)
        {
            try
            {
                //MusicList = new ObservableCollection<Music>(_allMusic);
                groupedByFirstLetter = MusicList
                        .GroupBy(item => ToolUtils.GetFirstLetterAdvanced(item.Author))
                        .OrderBy(group => group.Key)
                        .Select(group => new MusicGroup(group.Key, group.ToList()))
                        .ToList();
                GroupedMusicViewSource.Source = groupedByFirstLetter;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }

        //public void SortMusicList(string sortOrder)
        //{
        //    //if (_allMusic.Count > 0)
        //    //{
        //    //    MusicList.Clear();
        //    //MusicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("artistCover", sortOrder, MusicList));
        //        //LoadMoreArtistAsync(true);
        //    //}
        //}

        public void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music artist = item.Content as Music;
                if (parentPage is not null && _musicBrowseViewModel is not null)
                {
                    //parentPage.LoadArtistMusic(artist);
                    _musicBrowseViewModel.PageType = "artist";
                    _musicBrowseViewModel.paramName = artist.Author;
                    //currentArtistName = artist.Author;
                    _musicBrowseViewModel.CurrentArtist = artist;
                    _musicBrowseViewModel.currentPage = typeof(SongCollectionPage);
                    parentPage.NavigatePage(_musicBrowseViewModel.currentPage, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                }
            }
        }

        public async void Artist_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;
            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);
            if (clickedItem is not null)
            {
                // 从 GridViewItem 获取数据项
                var artist = clickedItem.Content as Music;

                if (artist is not null)
                {
                    // 显示专辑右键菜单
                    await _contextMenuService.ShowAlbumContextMenu(
                        artist,
                        originalSource,
                        e.GetPosition(originalSource),
                        "artist"
                    );
                    _contextMenuService.playingArtistMusic += PlayingArtist;
                }
            }
            e.Handled = true;
        }

        private void PlayingArtist(object? sender, Music e)
        {
            List<Music> artists = (MusicDatabaseService.GetArtistMusicFromMem(e.Author)).OrderBy(m => m.Album).ToList();
            if (artists is not null && artists.Count > 0)
            {
                if (parentPage is not null)
                {
                    parentPage.ViewModel.SequentialPlayingList = new ObservableCollection<Music>(artists);
                    parentPage.PlayMusic(music: artists[0], IsChangeList: true);
                }
            }
        }
    }
}
