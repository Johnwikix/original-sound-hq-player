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
    public partial class FolderViewModel : ObservableObject
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
        private string _lastSearchText = "";
        private MusicBrowsePage? parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        private AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FolderBrowsePage? currentPage { get; set; }
        private ContextMenuService _contextMenuService { get; }

        public FolderViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel? musicBrowseViewModel, AppObservableObj appObservableObj, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
            GroupedMusicViewSource = new CollectionViewSource
            {
                IsSourceGrouped = true
            };
            //parentPage.DisableBackButton();
            parentPage.refreshPage += RefreshFolder;
            _contextMenuService = contextMenuService;
            _contextMenuService.playingFolderMusic += PlayingFolder;
            _contextMenuService.rescanFolderEnd += RescanFolderEnd;
            _contextMenuService.showTransmission += (s, eventArgs) =>
            {
                if (parentPage is not null)
                {
                    parentPage.ShowTransmission();
                }
            };
            _contextMenuService.hideTransmission += (s, eventArgs) =>
            {
                if (parentPage is not null)
                {
                    parentPage.HideTransmission();
                }
            };
            _musicBrowseViewModel = musicBrowseViewModel;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
        }

        public void UpdateUsbIcon()
        {
            ToolUtils.RefreshIcon(MusicList, "folder");
        }

        public void SetCurrentPage(FolderBrowsePage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            AppObservableObj.CurrentFolderObj = null;
            AppObservableObj.PageType = "folderBrowse";

            if (_lastSearchText != AppData.searchText || MusicList is null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeData();
            }
            else
            {
                Debug.WriteLine("搜索条件未变更，保留当前视图状态");
            }

            ToolUtils.RefreshIcon(MusicList, "folder");
        }

        private void RefreshFolder(object? sender, bool e)
        {
            InitializeData();
        }

        private void InitializeData()
        {
            try
            {
                MusicList.Clear();
                var query = (_musicDatabaseService.GetMusicListFromMemWithFolderSearchOption(AppData.searchText))
                        .AsValueEnumerable()
                        .GroupBy(m => m.LastLevelFolderPath)
                        .Select(g => g.AsValueEnumerable().First())
                        .OrderBy(m => m.LastLevelFolderPath);
                foreach (var music in query)
                {
                    MusicList.Add(music);
                }
                LoadMoreFolderAsync(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化文件夹时出错: {ex.Message}");
            }
        }

        private void LoadMoreFolderAsync(bool isFirstLoad = false)
        {
            try
            {
                groupedByFirstLetter = MusicList.AsValueEnumerable()
                        .GroupBy(item => ToolUtils.GetFirstLetterAdvanced(item.LastLevelFolderPath))
                        .OrderBy(group => group.Key)
                        .Select(group => new MusicGroup(group.Key, group.AsValueEnumerable().ToList()))
                        .ToList();
                GroupedMusicViewSource.Source = groupedByFirstLetter;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载文件夹数据失败: {ex.Message}");
            }
        }

        public void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item is not null)
            {
                Music folder = item.Content as Music;
                if (parentPage is not null && _musicBrowseViewModel is not null && folder is not null)
                {
                    try
                    {
                        AppObservableObj.PageType = "folder";
                        AppObservableObj.CurrentFolderObj = folder;
                        //_musicBrowseViewModel.paramName = folder.LastLevelFolderPath;
                        //_musicBrowseViewModel.CurrentFolder = folder;
                        //_musicBrowseViewModel.currentPage = typeof(SongCollectionPage);
                        parentPage.NavigatePage(typeof(SongFolderListPage), new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }
                }
            }
        }

        public async void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem is not null)
            {
                var folder = clickedItem.Content as Music;

                if (folder is not null)
                {
                    await _contextMenuService.ShowAlbumContextMenu(
                        folder,
                        originalSource,
                        e.GetPosition(originalSource),
                        "folder"
                    );
                }
            }

            e.Handled = true;
        }

        private void RescanFolderEnd(object? sender, EventArgs e)
        {
            var mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow is not null)
            {
                mainWindow.UpdateMusicList();
            }
        }

        private void PlayingFolder(object? sender, Music e)
        {
            List<Music> folders = (_musicDatabaseService.GetFolderMusicFromMem(e.LastLevelFolderPath)).AsValueEnumerable().OrderBy(m => m.Album).ToList();
            if (folders is not null && folders.Count > 0)
            {
                if (parentPage is not null)
                {
                    AppObservableObj.SequentialPlayingList = new ObservableCollection<Music>(folders);
                    parentPage.PlayMusic(music: folders[0], IsChangeList: true);
                }
            }
        }
    }
}
