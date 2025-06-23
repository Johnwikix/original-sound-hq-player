using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class FavouritePlayListViewModel : ObservableObject
    {
        public EventHandler MusicListViewUpdated;        
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }

        public FavouritePlayListViewModel()
        {
            InitializeData();
        }

        public void InitializeData()
        {
            var musicList = MusicDatabaseService.GetFavoriteMusicFromMem(AppData.searchText);
            LoadMusicAsync(musicList);
        }

        public void LoadMusicAsync(List<Music> musics)
        {
            try
            {
                MusicList.Clear();
                foreach (Music music in musics)
                {
                    MusicList.Add(music);
                }
                SortMusicList(AppData.sortOrder);
                MusicListViewUpdated?.Invoke(this, EventArgs.Empty);
                //UpdateMusicListView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载音乐列表失败: {ex.Message}");
            }
        }

        public async Task DragItems()
        {
            for (int i = 0; i < MusicList.Count; i++)
            {
                MusicList[i].Order = MusicList.Count - i;
                await MusicDatabaseService.UpdateMuisc(MusicList[i]);
            }
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        }

        public void ClearUsbDeviceMusicList() {
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;
            }
        }

        public void RefreshUsbDeviceMusicList() {
            var usbMusicGroups = AppData.musicOnUsbDevice
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var music in MusicList)
            {
                music.IsExistOnDevice = 0;

                if (usbMusicGroups.TryGetValue(music.Title, out var matchingItems))
                {
                    music.IsExistOnDevice = 1;
                    foreach (var usbMusic in matchingItems)
                    {
                        if (music.Author == usbMusic.Author &&
                            music.Album == usbMusic.Album &&
                            music.Extension == usbMusic.Extension)
                        {
                            music.IsExistOnDevice = 2;
                            break;
                        }
                    }
                }
            }
        }

        public void SortMusicList(string sortOrder)
        {
            var order = "DefaultOrder";
            List<Music> musics = new List<Music>();
            if (!string.IsNullOrEmpty(sortOrder))
            {
                order = sortOrder;
            }
            if (MusicList.Count > 0)
            {
                musics = ToolUtils.SortMusicList("favour", order, MusicList.ToList());
            }
            MusicList.Clear();
            foreach (Music music in musics)
            {
                MusicList.Add(music);
            }
        }

        public void AddMusicToTop(Music newMusic)
        {
            int maxOrder = MusicList.Any() ? MusicList.Max(m => m.Order) : 0;
            newMusic.Order = maxOrder + 1;
            MusicList.Insert(0, newMusic);
        }

        public void RemoveMusic(Music musicToRemove)
        {
            var music = MusicList.FirstOrDefault(m => m.Id == musicToRemove.Id);
            if (music != null)
            {
                MusicList.Remove(music);
            }
        }
    }
}
