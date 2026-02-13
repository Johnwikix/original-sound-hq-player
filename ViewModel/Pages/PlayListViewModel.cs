using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class PlayListViewModel : ObservableObject
    {
        private MusicBrowsePage? _parentPage;
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        private PlayListPage? _currentPage { get; set; }

        public PlayListViewModel(MusicBrowsePage parent, MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            //_parentPage.addPlayListEvent += RefreshPlayList;
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }

        public void SetCurrentPage(PlayListPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentPlayList = null;
            App.MainWindow.IsBackBtnEnable = false;
            //_parentPage.DisableBackButton();
            //InitializingData();
        }

        //private void RefreshPlayList(object? sender, PlayList playList)
        //{
        //    PlayLists.Add(playList);
        //}

        //public async void InitializingData()
        //{
        //    try
        //    {
        //        var playlists = await _musicDatabaseService.GetPlayListAsync();
        //        PlayLists.Clear();

        //        foreach (var playlist in playlists)
        //        {
        //            PlayLists.Add(playlist);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"加载播放列表时出错: {ex.Message}");
        //    }
        //}

        //public void MainWindow_PlayListLoaded(object? sender, List<PlayList> playLists)
        //{
        //    try
        //    {
        //        PlayLists.Clear();

        //        foreach (var playlist in playLists)
        //        {
        //            PlayLists.Add(playlist);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"更新播放列表时出错: {ex.Message}");
        //    }
        //}

        public async void RemovePlayList(PlayList playList)
        {
            if (playList is null) return;

            await _musicDatabaseService.RemovePlayList(playList);
            AppViewModel.AllPlayList.Remove(playList);
        }

        public void PlayListView_SelectionChanged(PlayList? playList)
        {
            if (playList is not null && _parentPage is not null && _musicBrowseViewModel is not null)
            {
                AppViewModel.PageType = "playlist";
                //_musicBrowseViewModel.paramName = playList.Name;
                AppViewModel.CurrentPlayList = playList;
                AppViewModel.CurrentPlayListId = playList.Id;
                AppData.CurrentPage = typeof(PlayListSongPage);
                _parentPage.NavigatePage(AppData.CurrentPage, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
        }

        public void ExportPlayList(PlayList playList)
        {
            ToolUtils.ExportPlayList(playList);
        }
    }
}
