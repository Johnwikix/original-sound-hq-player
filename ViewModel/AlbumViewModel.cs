using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
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
            else
            {
                Debug.WriteLine("搜索条件未变更，保留当前视图状态");
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
                //await AlbumCoverService.LoadAlbumCoversAsync(_allMusic);
                // 页面已经显示，现在开始异步加载封面
                // 使用 Task.Run 避免阻塞 UI 线程
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount * 2
                };
                _ = Task.Run(async () =>
                {
                    Parallel.ForEach(MusicList, options, async music =>
                    {
                        try
                        {
                            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
                            {
                                music.Cover = cachedCover;
                            }
                            else
                            {
                                BitmapImage cover = await ToolUtils.GetAlbumCover(music, AppSettings.CoverSize);
                                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                                {
                                    music.Cover = cover;
                                });
                                if (AppSettings.isCoverCacheEnabled)
                                {
                                    AppData.albumCoverCache[music.Album] = cover;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"加载专辑封面失败: {ex.Message}");
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载专辑数据失败: {ex.Message}");
            }
        }
    }
}
