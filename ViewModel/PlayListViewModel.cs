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
        private ObservableCollection<PlayList> _playLists = new ObservableCollection<PlayList>();
        public ObservableCollection<PlayList> PlayLists
        {
            get => _playLists;
            set => SetProperty(ref _playLists, value);
        }

        private MusicBrowsePage? _parentPage;
        private PlayListPage? _currentPage;

        public PlayListViewModel()
        {
        }

        public void SetCurrentPage(PlayListPage page)
        {
            _currentPage = page;
        }

        public void SetParentPage(MusicBrowsePage parent)
        {
            _parentPage = parent;
            _parentPage.currentPlayList = null;
            _parentPage.DisableBackButton();
            _parentPage.refreshPage += RefreshPlayList;
            InitializingData();
        }

        private void RefreshPlayList(object? sender, EventArgs e)
        {
            InitializingData();
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
                InitializingData();
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
