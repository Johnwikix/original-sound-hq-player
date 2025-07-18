using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;

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
        private PlayListPage? _currentPage;

        public PlayListViewModel(MusicBrowsePage parent)
        {
            _parentPage = parent;            
            _parentPage.addPlayListEvent += RefreshPlayList;
        }

        public void SetCurrentPage(PlayListPage page)
        {
            _currentPage = page;
        }

        public void ReceiveNavigation()
        {
            _parentPage.currentPlayList = null;
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
                var playlists = await MusicDatabaseService.GetPlayListAsync();
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
            if (playList == null) return;

            await MusicDatabaseService.RemovePlayList(playList);
            PlayLists.Remove(playList);
        }

        public async void EditPlayListName(PlayList playList, Func<Task<string>> getNameCallback)
        {
            if (playList == null || getNameCallback == null) return;

            string newName = await getNameCallback();

            if (!string.IsNullOrEmpty(newName))
            {
                playList.Name = newName;
                await MusicDatabaseService.UpdatePlayList(playList);
                var existingPlayList = PlayLists.FirstOrDefault(p => p.Id == playList.Id);
                if (existingPlayList != null)
                {
                    existingPlayList.Name = newName;
                }
            }
        }

        public void PlayListView_SelectionChanged(PlayList? playList)
        {
            if (playList != null && _parentPage != null)
            {
                _parentPage.LoadPlayListSong(playList);
            }
        }
    }
}
