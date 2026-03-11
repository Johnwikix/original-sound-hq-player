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
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService MusicDatabaseService { get; }

        public PlayListViewModel(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
        }

        public void ReceiveNavigation()
        {
            AppViewModel.CurrentPlayList = null;
            AppViewModel.IsBackBtnEnable = false;
        }     

        public async void RemovePlayList(PlayList playList)
        {
            if (playList is null) return;

            await MusicDatabaseService.RemovePlayList(playList);
            AppViewModel.AllPlayList.Remove(playList);
        }        

        public void ExportPlayList(PlayList playList)
        {
            ToolUtils.ExportPlayList(playList);
        }
    }
}
