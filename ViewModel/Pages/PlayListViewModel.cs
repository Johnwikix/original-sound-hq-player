using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class PlayListViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        private MusicDatabaseService MusicDatabaseService { get; }

        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }

        public PlayListViewModel(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
            AppViewModel.PropertyChanged += OnAppVmPropertyChanged;
        }

        private void OnAppVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.CurrentPlayList))
            {
                bool shouldBeInDetail = AppViewModel.CurrentPlayList is not null;
                if (IsInDetailMode != shouldBeInDetail)
                {
                    IsInDetailMode = shouldBeInDetail;
                }
                if (shouldBeInDetail)
                {
                    AppViewModel.PageType = "playlist";
                    AppViewModel.IsBackBtnEnable = true;
                }
                else
                {
                    AppViewModel.PageType = "playlistBrowse";
                    AppViewModel.IsBackBtnEnable = false;
                }
            }
        }

        public void ReceiveNavigation()
        {
            if (AppViewModel.CurrentPlayList is null)
            {
                AppViewModel.IsBackBtnEnable = false;
                if (IsInDetailMode) IsInDetailMode = false;
            }
            else
            {
                if (!IsInDetailMode) IsInDetailMode = true;
                AppViewModel.IsBackBtnEnable = true;
            }
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

        public void EnterPlayList(PlayList playList)
        {
            if (playList is null) return;
            AppViewModel.CurrentPlayList = playList;
            AppViewModel.CurrentPlayListId = playList.Id;
        }
    }
}
