using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FolderViewModel : ObservableObject
    {
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }

        private List<Music>? _allMusic;
        private string _lastSearchText = "";
        private MusicBrowsePage? parentPage;
        private FolderBrowsePage? currentPage;
        private ContextMenuService _contextMenuService;

        public FolderViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService)
        {
            parentPage = parent;
            parentPage.DisableBackButton();
            parentPage.refreshPage += RefreshFolder;
            parentPage.refreshUsbDeviceMusicList +=
                (s, e) =>
                {
                    ToolUtils.RefreshIcon(MusicList, "album");
                };
            parentPage.clearUsbDeviceMusicList +=
                (s, e) =>
                {
                    ToolUtils.RefreshIcon(MusicList, "album");
                };
            _contextMenuService = contextMenuService;
            _contextMenuService.playingFolderMusic += PlayingFolder;
            _contextMenuService.rescanFolderEnd += RescanFolderEnd;
            _contextMenuService.showTransmission += (s, eventArgs) =>
            {
                if (parentPage != null)
                {
                    parentPage.ShowTransmission();
                }
            };
            _contextMenuService.hideTransmission += (s, eventArgs) =>
            {
                if (parentPage != null)
                {
                    parentPage.HideTransmission();
                }
            };
        }

        public void SetCurrentPage(FolderBrowsePage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
           
            parentPage.currentFolderName = null;
            parentPage.pageType = null;         

            if (_lastSearchText != AppData.searchText || MusicList == null || MusicList.Count == 0)
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

        private void RefreshFolder(object? sender, EventArgs e)
        {
            InitializeData();
        }

        private void InitializeData()
        {
            try
            {
                //MusicList.Clear();
                if (parentPage != null)
                {
                    _allMusic = (MusicDatabaseService.GetMusicListFromMemWithFolderSearchOption(AppData.searchText))
                        .GroupBy(m => m.LastLevelFolderPath)
                        .Select(g => g.First())
                        .OrderBy(m => m.LastLevelFolderPath)
                        .ToList();
                    LoadMoreFolderAsync(true);
                }
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
                MusicList = new ObservableCollection<Music>(_allMusic);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载文件夹数据失败: {ex.Message}");
            }
        }

        public void SortMusicList(string sortOrder)
        {
            if (_allMusic != null && _allMusic.Count > 0)
            {
                //MusicList.Clear();
                MusicList = new ObservableCollection<Music>(ToolUtils.SortMusicList("folderCover", sortOrder, MusicList));
                //LoadMoreFolderAsync(true);
            }
        }

        public void FolderGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var gridView = sender as GridView;
            GridViewItem item = gridView.ContainerFromItem(e.ClickedItem) as GridViewItem;
            if (item != null)
            {
                Music folder = item.Content as Music;
                if (parentPage != null)
                {
                    parentPage.LoadFolderMusic(folder.LastLevelFolderPath);
                }
            }
        }

        public async void Folder_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var originalSource = e.OriginalSource as FrameworkElement;
            GridViewItem clickedItem = ToolUtils.FindParent<GridViewItem>(originalSource);

            if (clickedItem != null)
            {
                var folder = clickedItem.Content as Music;

                if (folder != null)
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
            if (mainWindow != null)
            {
                mainWindow.UpdateMusicList();
            }
        }

        private void PlayingFolder(object? sender, Music e)
        {
            List<Music> folders = (MusicDatabaseService.GetFolderMusicFromMem(e.LastLevelFolderPath)).OrderBy(m => m.Album).ToList();
            if (folders != null && folders.Count > 0)
            {
                if (parentPage != null)
                {
                    parentPage.ViewModel.CurrentPlayingList = new ObservableCollection<Music>(folders);
                    parentPage.PlayMusic(folders[0]);
                }
            }
        }
    }
}
