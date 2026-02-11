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
        private MusicBrowsePage? parentPage { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private FolderBrowsePage? currentPage { get; set; }
        private ContextMenuService _contextMenuService { get; }

        public FolderViewModel(MusicBrowsePage parent, ContextMenuService contextMenuService, MusicBrowseViewModel? musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            parentPage = parent;
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
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void UpdateUsbIcon()
        {
            //ToolUtils.RefreshIcon(MusicList, "folder");
        }

        public void SetCurrentPage(FolderBrowsePage page)
        {
            currentPage = page;
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentFolderObj = null;
            AppViewModel.PageType = "folderBrowse";
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
                        AppViewModel.PageType = "folder";
                        AppViewModel.CurrentFolderObj = folder;
                        parentPage.NavigatePage(typeof(SongFolderListPage),new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
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
                    AppViewModel.SequentialPlayingList = new(folders);
                    parentPage.PlayMusic(music: folders[0], IsChangeList: true);
                }
            }
        }
    }
}
