using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AlbumViewModel: ObservableObject
    {
        private ObservableCollection<Music> _musicList = new ObservableCollection<Music>();
        public ObservableCollection<Music> MusicList
        {
            get => _musicList;
            set => SetProperty(ref _musicList, value);
        }
        private List<Music> _allMusic = [];
        private string _lastSearchText = "";

        public void Entance()
        {
            if (_lastSearchText != AppData.searchText || MusicList == null || MusicList.Count == 0)
            {
                _lastSearchText = AppData.searchText;
                InitializeData();
            }
            ToolUtils.RefreshIcon(MusicList, "album");
        }

        public async void InitializeData()
        {
            MusicList.Clear();
            _allMusic = MusicDatabaseService.GetMusicListFromMem(AppData.searchText).GroupBy(m => m.Album).Select(g => g.First()).OrderBy(m => m.Album).ToList();
            await LoadMoreAlbumsAsync(true);            
        }
        public async Task SortMusicList(string sortOrder = "DefaultOrder")
        {
            if (_allMusic.Count > 0)
            {
                MusicList.Clear();
                _allMusic = ToolUtils.SortMusicList("albumCover", sortOrder, _allMusic.ToList());
                await LoadMoreAlbumsAsync(true);
            }
        }
        private async Task LoadMoreAlbumsAsync(bool isFirstLoad = false)
        {
            try
            {
                foreach (var item in _allMusic)
                {
                    MusicList.Add(item);
                }

                // 创建一个不会被提前释放的信号量
                var semaphore = new SemaphoreSlim(8, Environment.ProcessorCount);

                var visibleTasks = MusicList.Select(music => LoadSingleAlbumCoverAsync(music, semaphore)).ToArray();

                try
                {
                    await Task.WhenAll(visibleTasks);
                }
                finally
                {
                    // 确保所有任务完成后再释放信号量
                    semaphore.Dispose();
                }

                _ = Task.Delay(5000).ContinueWith(_ =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }            
        }

        private async Task LoadSingleAlbumCoverAsync(Music music, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        music.Cover = cachedCover;
                    });
                }
                else
                {
                    BitmapImage cover = await ToolUtils.GetAlbumCover(music, AppSettings.CoverSize);
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        music.Cover = cover;
                    });
                    if (AppSettings.isCoverCacheEnabled && cover != null)
                    {
                        //AppData.albumCoverCache[music.Album] = cover;
                        AppData.albumCoverCache.SetValue(music.Album, cover);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑封面失败: {ex.Message}");
            }
            finally
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 信号量已被释放，忽略此异常
                }
            }
        }
    }
}
