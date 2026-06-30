using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

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
            AppData.CurrentPage = typeof(PlayListPage);
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

        public async Task RemovePlayList(PlayList playList)
        {
            if (playList is null) return;

            await MusicDatabaseService.RemovePlayList(playList);
            AppViewModel.AllPlayList.Remove(playList);
        }

        public async Task ExportPlayList(PlayList playList)
        {
            await ToolUtils.ExportPlayList(playList);
        }

        public void EnterPlayList(PlayList playList)
        {
            if (playList is null) return;
            AppViewModel.CurrentPlayList = playList;
            AppViewModel.CurrentPlayListId = playList.Id;
        }

        public async Task InsertPlayList(PlayList newPlaylist)
        {
            await MusicDatabaseService.InsertPlayList(newPlaylist);
        }
    }
}
