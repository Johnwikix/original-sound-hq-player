using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            //AppViewModel.IsSortComboBoxVisible = false;
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

        public void ExportPlayList(PlayList playList)
        {
            ToolUtils.ExportPlayList(playList);
        }
    }
}
