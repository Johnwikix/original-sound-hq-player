using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TagLib.Ape;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Services;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class AppObservableObj : ObservableObject
    {
        // 简单属性重构
        public Music? CurrentArtistObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    ArtistSongsView.RefreshFilter();
                }
            }
        }
        public Music? CurrentAlbumObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AlbumSongsView.RefreshFilter();
                }
            }
        }
        public Music? CurrentFolderObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    FolderSongsView.RefreshFilter();
                }
            }
        }
        public PlayMode CurrentPlayMode
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (value == PlayMode.RandomLoop)
                    {
                        CurrentPlayingList = SequentialPlayingList.CreateShuffled();
                    }
                    else
                    {
                        CurrentPlayingList = SequentialPlayingList;
                    }
                }
            }
        } = PlayMode.ListLoop;
        private CancellationTokenSource _searchCts;
        public string SearchText
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _ = OnSearchTextChangedAsync();
                }
            }
        }
        public ObservableCollection<Music> AllSongs { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
        public Music FavoriteSongsSelectedMusic { get; set => SetProperty(ref field, value); }
        //public AdvancedCollectionView FavoriteSongsView { get; }
        public AdvancedCollectionView AllSongsView { get; }
        public AdvancedCollectionView AlbumSongsView { get; }
        public AdvancedCollectionView ArtistSongsView { get; }
        public AdvancedCollectionView FolderSongsView { get; }
        public Music CurrentPlayingMusic { get; set => SetProperty(ref field, value); }
        public string PlayModeFlyoutText { get; set => SetProperty(ref field, value); }
        public ObservableCollection<Music> SequentialPlayingList 
        { 
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (CurrentPlayMode == PlayMode.RandomLoop)
                    {
                        CurrentPlayingList = value.CreateShuffled();
                    }
                    else {
                        CurrentPlayingList = value;
                    }
                }
            }
        }
        public ObservableCollection<Music> CurrentPlayingList { get; set => SetProperty(ref field, value); }
        public SortOption SelectedSortOption
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnSelectSortChanged();
                }
            }
        }
        public IEnumerable<SortOption> AllSortOptions = [
           new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("A-Z", "SortOrderA_Z"),
            new SortOption("Artist", "SortOrderArtist"),
            new SortOption("Album", "SortOrderAlbum"),
            new SortOption("CreateTimeASC", "SortOrderCreateTimeASC"),
            new SortOption("CreateTimeDESC", "SortOrderCreateTimeDESC"),
            new SortOption("UpdateTimeASC", "SortOrderUpdateTimeASC"),
            new SortOption("UpdateTimeDESC", "SortOrderUpdateTimeDESC")
       ];
        public IEnumerable<SortOption> AlbumSortOptions = [
            new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("Artist", "SortOrderArtist")
        ];

        public ObservableCollection<SortOption> SortOptions { get; set => SetProperty(ref field, value); }
        public string MusicInfo { get; set => SetProperty(ref field, value); }
        public BitmapImage MusicDetailCover { get; set => SetProperty(ref field, value); }
        public bool IsMuted { get; set => SetProperty(ref field, value); } = false;
        public double TempVolume { get; set => SetProperty(ref field, value); } = 50;
        public string PlayTimeText { get; set => SetProperty(ref field, value); } = "00:00/00:00";
        public double ProgressSliderMax { get; set => SetProperty(ref field, value); } = 100;
        public ObservableCollection<LyricLine> UILyrics { get; set => SetProperty(ref field, value); } = [];
        public int LastLyricIndex { get; set => SetProperty(ref field, value); } = -1;
        public ImageSource? LyricPageBackgroundSource { get; set => SetProperty(ref field, value); } = null;
        public bool IsInitialized { get; set => SetProperty(ref field, value); } = false;
        public Visibility UsbDeviceVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public ObservableCollection<UsbStorageDevice> UsbStorageDevices { get; set => SetProperty(ref field, value); }
        public int UsbSelectedIndex { get; set => SetProperty(ref field, value); } = 0;
        public Visibility ProcessRingVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public bool IsFullScreen { get; set => SetProperty(ref field, value); } = false;
        public string InfoBarTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public bool InfoBarIsOpen { get; set => SetProperty(ref field, value); } = false;
        public string InfoBarMessage { get; set => SetProperty(ref field, value); } = string.Empty;
        public string PageType { get; set => SetProperty(ref field, value); } = string.Empty;
        public bool IsInPlayingDetailMode { get; set => SetProperty(ref field, value); } = false;
        public bool IsAcrylicBrushOpacity { get; set => SetProperty(ref field, value); } = false;
        public float TopControlsOpacity { get; set => SetProperty(ref field, value); } = 1.0f;

        // 带有复杂逻辑的属性重构
        public double Volume
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        if (value > 0) IsMuted = false;
                        if (!IsMuted) TempVolume = value;

                        App.Services.GetRequiredService<BassPlayerCommandService>().SetVolume(value / 100);
                    }
                }
            }
        } = 50;

        public Thickness LyricsMargin
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _ = _musicDatabaseService.SaveSettingAsync();
                }
            }
        }

        public bool IsPlayDetailButtonVisible
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        private MusicDatabaseService _musicDatabaseService { get; }

        public AppObservableObj(MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;
            AllSongsView = new AdvancedCollectionView(AllSongs, true) {
                Filter = item =>
                {
                    var m = (Music)item;
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        string query = SearchText.ToLower();
                        bool matches = (m.Title?.ToLower().Contains(query) ?? false) ||
                                       (m.Author?.ToLower().Contains(query) ?? false) ||
                                       (m.Album?.ToLower().Contains(query) ?? false);
                        if (!matches) return false;
                    }
                    return true;
                }
            };
            AllSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));

            AlbumSongsView = new AdvancedCollectionView(AllSongs, true)
            {
                Filter = item =>
                {
                    if (item is not Music m) return false;
                    if (CurrentAlbumObj != null)
                    {
                        if (!string.Equals(m.Album, CurrentAlbumObj.Album, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    // 2. 搜索过滤逻辑
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        return (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
                    }
                    return true;
                }
            };
            AlbumSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.TrackNumber), SortDirection.Ascending));

            ArtistSongsView = new AdvancedCollectionView(AllSongs, true)
            {
                Filter = item =>
                {
                    if (item is not Music m) return false;
                    if (CurrentArtistObj != null)
                    {
                        if (!string.Equals(m.Author, CurrentArtistObj.Author, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        return (m.Title?.ToLower().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                       (m.Album?.ToLower().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
                    }
                    return true;
                }
            };
            ArtistSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));

            FolderSongsView = new AdvancedCollectionView(AllSongs, true)
            {
                Filter = item =>
                {
                    if (item is not Music m) return false;
                    if (CurrentFolderObj != null)
                    {
                        if (!string.Equals(m.LastLevelFolderPath, CurrentFolderObj.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
       
                        return (m.Title?.ToLower().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                       (m.Author?.ToLower().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                       (m.Album?.ToLower().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
                    }
                    return true;
                }
            };
            FolderSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));
        }

        public async Task AddToFavourite(Music music)
        {
            music.IsFavorite = !music.IsFavorite;
            await _musicDatabaseService.AddToFavourite(music);
            if (CurrentPlayingMusic?.Id == music.Id)
            {
                CurrentPlayingMusic.IsFavorite = music.IsFavorite;
            }
        }

        public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        {
            int index = GetCurrentIndex();

            if (index != -1 && uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
            {
                var existingIds = new HashSet<int>(CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                var newMusicsToAdd = uniqueSelectedMusics.AsValueEnumerable()
                    .Where(music => !existingIds.Contains(music.Id)).ToList();
                for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
                {
                    CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
                }
            }
        }

        public void AddMusicToCurrentPlayList(Music music)
        {
            int index = GetCurrentIndex();
            if (index != -1 && music is not null)
            {
                var existingIds = new HashSet<int>(CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
                if (!existingIds.Contains(music.Id))
                {
                    CurrentPlayingList.Insert(index + 1, music);
                }
            }
        }

        public int GetCurrentIndex()
        {
            for (int i = 0; i < CurrentPlayingList.Count; i++)
            {
                if (CurrentPlayingList[i].Id == CurrentPlayingMusic.Id)
                    return i;
            }
            return -1;
        }

        private async Task OnSearchTextChangedAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                {
                    RefreshFavoriteMapping();
                    AllSongsView.RefreshFilter();
                    AlbumSongsView.RefreshFilter();
                    ArtistSongsView.RefreshFilter();
                    FolderSongsView.RefreshFilter();
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        public void RefreshFavoriteMapping()
        {
            var query = AllSongs.Where(m => m.IsFavorite);
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            IEnumerable<Music> sortedQuery = SelectedSortOption?.Tag switch
            {
                "A-Z" => query.OrderBy(m => m.Title),
                "Artist" => query.OrderBy(m => m.Author),
                "Album" => query.OrderBy(m => m.Album),
                "CreateTimeASC" => query.OrderBy(m => m.CreateTime),
                "CreateTimeDESC" => query.OrderByDescending(m => m.CreateTime),
                "UpdateTimeDESC" => query.OrderByDescending(m => m.UpdateTime),
                "DefaultOrder" => query.OrderByDescending(m => m.Order),
                _ => query.OrderByDescending(m => m.Order)
            };
            // 3. 转化为 List 执行查询
            var results = sortedQuery.ToList();
            FavoriteSongs.Clear();
            foreach (var music in results)
            {
                FavoriteSongs.Add(music);
            }
        }

        public void OnSelectSortChanged()
        {
            if (SelectedSortOption == null) return;
            UpdateViewSort(AllSongsView);
            UpdateViewSort(AlbumSongsView);
            UpdateViewSort(ArtistSongsView);
            UpdateViewSort(FolderSongsView);
            UpdateCollectionSort(FavoriteSongs);
        }

        private void UpdateViewSort(AdvancedCollectionView view)
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();

                // 使用模式匹配或映射来简化逻辑
                var (propertyName, direction) = SelectedSortOption.Tag switch
                {
                    "A-Z" => (nameof(Music.Title), SortDirection.Ascending),
                    "Artist" => (nameof(Music.Author), SortDirection.Ascending),
                    "Album" => (nameof(Music.Album), SortDirection.Ascending),
                    "CreateTimeASC" => (nameof(Music.CreateTime), SortDirection.Ascending),
                    "CreateTimeDESC" => (nameof(Music.CreateTime), SortDirection.Descending),
                    "UpdateTimeDESC" => (nameof(Music.UpdateTime), SortDirection.Descending),
                    "DefaultOrder" => (nameof(Music.Title), SortDirection.Descending),
                    _ => (nameof(Music.Title), SortDirection.Ascending)
                };

                view.SortDescriptions.Add(new SortDescription(propertyName, direction));
            }
        }

        private void UpdateCollectionSort(ObservableCollection<Music> collection)
        {
            if (collection == null || !collection.Any()) return;

            // 1. 获取排序规则
            var (propertyName, isAscending) = SelectedSortOption.Tag switch
            {
                "A-Z" => (nameof(Music.Title), true),
                "Artist" => (nameof(Music.Author), true),
                "Album" => (nameof(Music.Album), true),
                "CreateTimeASC" => (nameof(Music.CreateTime), true),
                "CreateTimeDESC" => (nameof(Music.CreateTime), false),
                "UpdateTimeDESC" => (nameof(Music.UpdateTime), false),
                "DefaultOrder" => (nameof(Music.Order), false),
                _ => (nameof(Music.Title), true)
            };
            List<Music> sortedList;

            if (isAscending)
            {
                sortedList = collection.OrderBy(m => GetPropertyValue(m, propertyName)).ToList();
            }
            else
            {
                sortedList = collection.OrderByDescending(m => GetPropertyValue(m, propertyName)).ToList();
            }

            collection.Clear();
            foreach (var item in sortedList)
            {
                collection.Add(item);
            }
        }

        // 辅助方法：通过属性名获取值
        private object GetPropertyValue(Music m, string propName)
        {
            return m.GetType().GetProperty(propName)?.GetValue(m, null);
        }

        /// <summary>
        /// 当歌曲被设为收藏时调用
        /// </summary>
        public void AddToFavoriteSongs(Music music)
        {
            FavoriteSongs.Insert(0, music);
        }

        /// <summary>
        /// 当歌曲被取消收藏时调用
        /// </summary>
        public void RemoveFromFavoriteSongs(Music music)
        {
            if (FavoriteSongs.Contains(music))
            {
                FavoriteSongs.Remove(music);
            }
        }

        public void ClearUsbDevice()
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var music in AllSongs)
                {
                    music.IsExistOnDevice = 0;
                }
            });
        }

        public void RefreshUsbDeviceMusicList()
        {
            var usbMusicGroups = AppData.musicOnUsbDevice.AsValueEnumerable()
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.AsValueEnumerable().ToList());
            foreach (var music in AllSongs)
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
    }
}