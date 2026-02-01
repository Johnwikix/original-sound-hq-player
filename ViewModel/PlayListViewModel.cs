using CommunityToolkit.Mvvm.ComponentModel;
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
        private ObservableCollection<PlayList> _playLists = [];
        public ObservableCollection<PlayList> PlayLists
        {
            get => _playLists;
            set => SetProperty(ref _playLists, value);
        }

        private MusicBrowsePage? _parentPage;
        private AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private MusicBrowseViewModel? _musicBrowseViewModel { get; }
        private PlayListPage? _currentPage { get; set; }

        public PlayListViewModel(MusicBrowsePage parent, MusicBrowseViewModel musicBrowseViewModel, AppObservableObj appObservableObj,MusicDatabaseService musicDatabaseService)
        {
            _parentPage = parent;
            _musicBrowseViewModel = musicBrowseViewModel;
            _parentPage.addPlayListEvent += RefreshPlayList;
            AppObservableObj = appObservableObj;
            _musicDatabaseService = musicDatabaseService;
        }

        public void SetCurrentPage(PlayListPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            _parentPage.ViewModel.currentPlayList = null;
            _parentPage.DisableBackButton();
            InitializingData();
        }

        private void RefreshPlayList(object? sender, PlayList playList)
        {
            PlayLists.Add(playList);
        }

        public async void InitializingData()
        {
            try
            {
                var playlists = await _musicDatabaseService.GetPlayListAsync();
                PlayLists.Clear();

                foreach (var playlist in playlists)
                {
                    PlayLists.Add(playlist);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载播放列表时出错: {ex.Message}");
            }
        }

        public void MainWindow_PlayListLoaded(object? sender, List<PlayList> playLists)
        {
            try
            {
                PlayLists.Clear();

                foreach (var playlist in playLists)
                {
                    PlayLists.Add(playlist);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新播放列表时出错: {ex.Message}");
            }
        }

        public async void RemovePlayList(PlayList playList)
        {
            if (playList is null) return;

            await _musicDatabaseService.RemovePlayList(playList);
            PlayLists.Remove(playList);
        }

        public async void EditPlayListName(PlayList playList, Func<Task<string>> getNameCallback)
        {
            if (playList is null || getNameCallback is null) return;

            string newName = await getNameCallback();

            if (!string.IsNullOrEmpty(newName))
            {
                playList.Name = newName;
                await _musicDatabaseService.UpdatePlayList(playList);
                var existingPlayList = PlayLists.AsValueEnumerable().FirstOrDefault(p => p.Id == playList.Id);
                if (existingPlayList is not null)
                {
                    existingPlayList.Name = newName;
                }
            }
        }

        public void PlayListView_SelectionChanged(PlayList? playList)
        {
            if (playList is not null && _parentPage is not null && _musicBrowseViewModel is not null)
            {
                AppObservableObj.PageType = "playlist";
                _musicBrowseViewModel.paramName = playList.Name;
                _musicBrowseViewModel.currentPlayList = playList;
                _musicBrowseViewModel.currentPlayListId = playList.Id;
                _musicBrowseViewModel.currentPage = typeof(PlayListSongPage);
                _parentPage.NavigatePage(_musicBrowseViewModel.currentPage, new DrillInNavigationTransitionInfo(), AppSettings.DrillInAnimationTime);
            }
        }

        public void ExportPlayList(PlayList playList)
        {
            ToolUtils.ExportPlayList(playList);
        }
    }
}
