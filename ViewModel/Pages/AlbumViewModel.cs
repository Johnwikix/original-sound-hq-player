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
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel : ObservableObject
    {
        //private ObservableCollection<Music> _musicList = [];
        //public ObservableCollection<Music> MusicList
        //{
        //    get => _musicList;
        //    set => SetProperty(ref _musicList, value);
        //}
        //private CollectionViewSource _groupedMusicViewSource;
        //public CollectionViewSource GroupedMusicViewSource
        //{
        //    get => _groupedMusicViewSource;
        //    set => SetProperty(ref _groupedMusicViewSource, value);
        //}
        //private List<MusicGroup> _groupedByFirstLetter = [];

        //private readonly Queue<MusicGroup> _musicGroupPool = new();

        //private string _lastSearchText = "";

        private MusicBrowsePage? parentPage;
        private MusicBrowseViewModel? _musicBrowseViewModel;
        private MusicDatabaseService _musicDatabaseService { get; }
        public AppViewModel AppViewModel { get; }
        private AlbumPage? currentPage;
        private ContextMenuService _contextMenuService;

        public AlbumViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            _contextMenuService = contextMenuService;
            _contextMenuService.playingAlbumMusic += PlayingAlbum;
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
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void UpdateUsbIcon()
        {
            //ToolUtils.RefreshIcon(MusicList, "album");
        }

        public void SetCurrentPage(AlbumPage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentAlbumObj = null;
            AppViewModel.PageType = "albumBrowse";
            //parentPage.DisableBackButton();
            Entance();
            App.MainWindow.IsBackBtnEnable = false;
        }

        private void RefreshAlbum(object? sender, bool e)
        {
            InitializeData();
        }

        public void Entance()
        {
            //if (_lastSearchText != AppData.searchText || MusicList is null || MusicList.Count == 0)
            //{
            //    _lastSearchText = AppData.searchText;
            //    InitializeData();
            //}
            //ToolUtils.RefreshIcon(MusicList, "album");
            //_musicBrowseViewModel?.AlbumSortOptions();
            //SortMusicList(AppData.sortOrder);
        }

        public void InitializeData()
        {
            //MusicList.Clear();
            //var query = _musicDatabaseService.GetMusicListFromMem(AppData.searchText)
            //    .AsValueEnumerable()
            //    .GroupBy(m => m.Album)
            //    .Select(g => g.AsValueEnumerable().First())
            //    .OrderBy(m => m.Album);
            //foreach (var music in query)
            //{
            //    MusicList.Add(music);
            //}
            //LoadMoreAlbumsAsync(true);
        }
        //public void SortMusicList(string sortOrder = "DefaultOrder", bool isSort = true)
        //{
        //    if (_currentSortOrder == sortOrder && isSort)
        //        return;

        //    _currentSortOrder = sortOrder;

        //    foreach (var group in _groupedByFirstLetter)
        //    {
        //        group.Clear();
        //        _musicGroupPool.Enqueue(group);
        //    }
        //    _groupedByFirstLetter.Clear();

        //    System.Linq.ILookup<string, Music> groupedMusic;

        //    if (sortOrder == "Artist")
        //    {
        //        groupedMusic = MusicList.AsValueEnumerable()
        //        .ToLookup(item => ToolUtils.GetFirstLetterAdvanced(item.Author));
        //    }
        //    else
        //    {
        //        groupedMusic = MusicList.AsValueEnumerable()
        //            .ToLookup(item => ToolUtils.GetFirstLetterAdvanced(item.Album));
        //    }
        //    var sortedGroups = groupedMusic.AsValueEnumerable()
        //        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase); // 确保按字母顺序排序，不区分大小写
        //    // 重用或创建MusicGroup对象
        //    foreach (var group in sortedGroups)
        //    {
        //        MusicGroup musicGroup;

        //        if (_musicGroupPool.Count > 0)
        //        {
        //            // 从对象池获取并重新初始化
        //            musicGroup = _musicGroupPool.Dequeue();
        //            musicGroup.Key = group.Key;
        //            musicGroup.AddRange(group);
        //        }
        //        else
        //        {
        //            // 创建新对象
        //            musicGroup = new MusicGroup(group.Key, group);
        //        }

        //        _groupedByFirstLetter.Add(musicGroup);
        //    }

        //    GroupedMusicViewSource.Source = _groupedByFirstLetter;
        //}
        //private void LoadMoreAlbumsAsync(bool isFirstLoad = false)
        //{
        //    try
        //    {
        //        SortMusicList(_currentSortOrder, false);
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
        //    }
        //}

        //public async void OnAlbumDetailChanged(object sender, Music cover)
        //{
        //    var musicToUpdate = MusicList.AsValueEnumerable()
        //        .FirstOrDefault(music => music.Album == cover.Album);
        //    if (musicToUpdate is not null)
        //    {
        //        musicToUpdate.Year = cover.Year;
        //        musicToUpdate.Album = cover.Album;
        //    }
        //}

        public void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            var item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music album = item.Content as Music;
                if (parentPage is not null && _musicBrowseViewModel is not null && album is not null)
                {
                    AppViewModel.PageType = "album";
                    AppViewModel.CurrentAlbumObj = album;
                    //_musicBrowseViewModel.paramName = album.Album;
                    //_musicBrowseViewModel.CurrentAlbum = album;
                    //_musicBrowseViewModel.currentPage = typeof(SongCollectionPage);
                    parentPage.NavigatePage(typeof(SongCollectionPage), new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);

                }
            }
        }

        public void PlayingAlbum(object? sender, Music e)
        {
            List<Music> albums = _musicDatabaseService.GetAlbumMusicFromMem(e.Album).AsValueEnumerable().OrderBy(m => m.Album).ToList();
            if (albums is not null && albums.Count > 0)
            {
                if (parentPage is not null && _musicBrowseViewModel is not null)
                {
                    AppViewModel.SequentialPlayingList = new(albums);
                    parentPage.PlayMusic(music: albums[0], IsChangeList: true);
                }
            }
        }

        public async void Album_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;

            // 向上遍历查找 GridViewItem
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem is not null)
            {
                // 从 GridViewItem 获取数据项
                var album = clickedItem.Content as Music;

                if (album is not null)
                {
                    _contextMenuService.SetAlbumPage(currentPage);
                    // 显示专辑右键菜单
                    await _contextMenuService.ShowAlbumContextMenu(
                        album,
                        originalSource,
                        e.GetPosition(originalSource),
                        "album"
                    );
                }
            }
            e.Handled = true;
        }

        public void Dispose()
        {
            //_musicGroupPool.Clear();
            //_groupedByFirstLetter.Clear();
        }
    }
}
