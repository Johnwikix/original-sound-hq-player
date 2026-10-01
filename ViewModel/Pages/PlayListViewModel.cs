using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.WinUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Linq;
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
        public WinUIMusicPlayer.State.AppState State => AppViewModel.State;
        private MusicDatabaseService MusicDatabaseService { get; }
        private MusicBrowseViewModel MusicBrowseViewModel { get; }

        public bool IsInDetailMode { get; set => SetProperty(ref field, value); }

        public bool HasPlayLists => AppViewModel.AllPlayList.Count > 0;

        public bool IsEditMode
        {
            get;
            set
            {
                if (!SetProperty(ref field, value)) return;
                OnPropertyChanged(nameof(EditModeText));
                if (!value) ClearSelectedPlayLists();
            }
        }

        public string EditModeText => ToolUtils.GetString(IsEditMode ? "PlayListEditDone" : "PlayListEdit");

        public ObservableCollection<PlayList> SelectedPlayLists { get; } = [];

        public int SelectedPlayListsCount => SelectedPlayLists.Count;

        public bool HasSelectedPlayLists => SelectedPlayLists.Count > 0;

        public bool CanDeleteSelectedPlayLists => HasSelectedPlayLists && !IsPlaylistOperationInProgress;

        public bool IsPlaylistOperationInProgress
        {
            get;
            private set
            {
                if (!SetProperty(ref field, value)) return;
                OnPropertyChanged(nameof(CanDeleteSelectedPlayLists));
            }
        }

        public PlayListViewModel(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, MusicBrowseViewModel musicBrowseViewModel)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
            MusicBrowseViewModel = musicBrowseViewModel;
            AppViewModel.PropertyChanged += OnAppVmPropertyChanged;
            AppViewModel.AllPlayList.CollectionChanged += OnAllPlayListsChanged;
        }

        private void OnAllPlayListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasPlayLists));
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
            if (playList is null || IsPlaylistOperationInProgress) return;

            IsPlaylistOperationInProgress = true;
            try
            {
                await MusicDatabaseService.RemovePlayList(playList);
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    if (AppViewModel.CurrentPlayList?.Id == playList.Id)
                    {
                        AppViewModel.CurrentPlayList = null;
                        AppViewModel.CurrentPlayListId = 0;
                    }
                    AppViewModel.AllPlayList.Remove(playList);
                    if (SelectedPlayLists.Remove(playList))
                    {
                        OnPropertyChanged(nameof(SelectedPlayListsCount));
                        OnPropertyChanged(nameof(HasSelectedPlayLists));
                        OnPropertyChanged(nameof(CanDeleteSelectedPlayLists));
                    }
                });
            }
            finally
            {
                IsPlaylistOperationInProgress = false;
            }
        }

        public void SetEditMode(bool enabled) => IsEditMode = enabled;

        public void SetSelectedPlayLists(IEnumerable<PlayList> playlists)
        {
            SelectedPlayLists.Clear();
            foreach (var playlist in playlists)
            {
                if (playlist is not null && !SelectedPlayLists.Contains(playlist))
                    SelectedPlayLists.Add(playlist);
            }
            OnPropertyChanged(nameof(SelectedPlayListsCount));
            OnPropertyChanged(nameof(HasSelectedPlayLists));
            OnPropertyChanged(nameof(CanDeleteSelectedPlayLists));
        }

        public void ClearSelectedPlayLists()
        {
            SelectedPlayLists.Clear();
            OnPropertyChanged(nameof(SelectedPlayListsCount));
            OnPropertyChanged(nameof(HasSelectedPlayLists));
            OnPropertyChanged(nameof(CanDeleteSelectedPlayLists));
        }

        public async Task DeleteSelectedPlayListsAsync()
        {
            var snapshot = SelectedPlayLists.ToArray();
            if (snapshot.Length == 0 || IsPlaylistOperationInProgress) return;

            IsPlaylistOperationInProgress = true;
            try
            {
                await MusicDatabaseService.RemovePlayLists(snapshot.Select(item => item.Id));
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    if (AppViewModel.CurrentPlayList is { } current
                        && snapshot.Any(item => item.Id == current.Id))
                    {
                        AppViewModel.CurrentPlayList = null;
                        AppViewModel.CurrentPlayListId = 0;
                    }

                    AppViewModel.AllPlayList.RemoveRange(snapshot);
                    ClearSelectedPlayLists();
                });
            }
            finally
            {
                IsPlaylistOperationInProgress = false;
            }
        }

        public async Task PersistPlayListOrderAsync()
        {
            if (IsPlaylistOperationInProgress) return;

            IsPlaylistOperationInProgress = true;
            var playlists = AppViewModel.AllPlayList;
            try
            {
                for (int i = 0; i < playlists.Count; i++)
                {
                    playlists[i].SortOrder = i + 1;
                }
                await MusicDatabaseService.UpdatePlayListOrderBatch(playlists);
            }
            finally
            {
                IsPlaylistOperationInProgress = false;
            }
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

        public async Task PlayPlayList(PlayList playList)
        {
            if (!AppViewModel.CanStartPlayback) return;
            if (playList is null) return;
            var items = MusicDatabaseService.GetMusicByPlayListIdFromMem(playList.Id, AppViewModel.SearchText);
            if (items is null) return;
            var arr = new BulkObservableCollection<Music>();
            foreach (var item in items)
            {
                if (item.Music is not null) arr.Add(item.Music);
            }
            if (arr.Count == 0) return;
            AppViewModel.SequentialPlayingList = arr;
            await MusicBrowseViewModel.PlayMusic(music: arr[0], IsChangeList: true);
        }
    }
}
