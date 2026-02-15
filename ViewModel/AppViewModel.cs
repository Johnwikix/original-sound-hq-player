using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public class AppViewModel : ObservableObject
    {
        public Music? CurrentArtistObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    //ArtistSongsView.RefreshFilter();
                    _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == value?.Author);
                }
            }
        }
        public Music? CurrentAlbumObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    //AlbumSongsView.RefreshFilter();
                    _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == value?.Album);
                }
            }
        }
        public Music? CurrentFolderObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    //FolderSongsView.RefreshFilter();
                    _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == value?.LastLevelFolderPath);
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
        public bool IsSortComboBoxVisible { get; set => SetProperty(ref field, value); } = true;
        public BulkObservableCollection<Music> AllSongs { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];        
        public ObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
        public PlayList CurrentPlayList { get; set => SetProperty(ref field, value); }
        public int CurrentPlayListId { get; set => SetProperty(ref field, value); }
        public ObservableCollection<GenericGroup> AlbumPageSource { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<GenericGroup> ArtistPageSource { get; set => SetProperty(ref field, value); } = [];
        public ObservableCollection<GenericGroup> FolderPageSource { get; set => SetProperty(ref field, value); } = [];
        public AdvancedCollectionView AllSongsView { get; }
        public BulkObservableCollection<Music> AlbumSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> ArtistSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FolderSongs { get; set => SetProperty(ref field, value); } = [];
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

        public ObservableCollection<SortOption> SortOptions { get; set => SetProperty(ref field, value); } = [
            new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("A-Z", "SortOrderA_Z"),
            new SortOption("Artist", "SortOrderArtist"),
            new SortOption("Album", "SortOrderAlbum"),
            new SortOption("CreateTimeASC", "SortOrderCreateTimeASC"),
            new SortOption("CreateTimeDESC", "SortOrderCreateTimeDESC"),
            new SortOption("UpdateTimeASC", "SortOrderUpdateTimeASC"),
            new SortOption("UpdateTimeDESC", "SortOrderUpdateTimeDESC")
        ];
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

        public AppViewModel(MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;       
            AllSongsView = new AdvancedCollectionView(AllSongs, true) // false 禁用内部反射排序
            {
                Filter = item =>
                {
                    if (item is not Music m) return false;
                    if (string.IsNullOrWhiteSpace(SearchText)) return true;

                    // AOT 安全且高性能的过滤逻辑
                    return (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                           (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                           (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
                }
            };
            AllSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));

            //AlbumSongsView = new AdvancedCollectionView(AllSongs, true)
            //{
            //    Filter = item =>
            //    {
            //        if (item is not Music m) return false;
            //        if (CurrentAlbumObj != null)
            //        {
            //            if (!string.Equals(m.Album, CurrentAlbumObj.Album, StringComparison.OrdinalIgnoreCase))
            //                return false;
            //        }
            //        // 2. 搜索过滤逻辑
            //        if (!string.IsNullOrWhiteSpace(SearchText))
            //        {
            //            return (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            //                   (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
            //        }
            //        return true;
            //    }
            //};
            //AlbumSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.TrackNumber), SortDirection.Ascending));

            //ArtistSongsView = new AdvancedCollectionView(AllSongs, true)
            //{
            //    Filter = item =>
            //    {
            //        if (item is not Music m) return false;
            //        if (CurrentArtistObj != null)
            //        {
            //            if (!string.Equals(m.Author, CurrentArtistObj.Author, StringComparison.OrdinalIgnoreCase))
            //                return false;
            //        }
            //        if (!string.IsNullOrWhiteSpace(SearchText))
            //        {
            //            return (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            //                           (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
            //        }
            //        return true;
            //    }
            //};
            //ArtistSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));

            //FolderSongsView = new AdvancedCollectionView(AllSongs, true)
            //{
            //    Filter = item =>
            //    {
            //        if (item is not Music m) return false;
            //        if (CurrentFolderObj != null)
            //        {
            //            if (!string.Equals(m.LastLevelFolderPath, CurrentFolderObj.LastLevelFolderPath, StringComparison.OrdinalIgnoreCase))
            //                return false;
            //        }
            //        if (!string.IsNullOrWhiteSpace(SearchText))
            //        {
       
            //            return (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            //                           (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
            //                           (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
            //        }
            //        return true;
            //    }
            //};
            //FolderSongsView.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));
            AllPlayList.CollectionChanged += AllPlayList_CollectionChanged;
            AllSongs.CollectionChanged += AllSongs_CollectionChanged;
        }

        private void AllSongs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (IsInitialized) {
                RefreshDataSource();
            }
        }

        private void AllPlayList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateMenuOptionsPlayList();
        }

        public void UpdateMenuOptionsPlayList() {
            App.Services.GetRequiredService<AlbumViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<ArtistViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<FolderViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<SongListViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<FavouritePlayListViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<SongArtistViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<SongCollectionViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<SongFolderListViewModel>().UpdateAlbumMenuOptionsPlayList();
        }

        public async Task UpdateSongCollectionsAsync(
        BulkObservableCollection<Music> targetCollection,
        SongViewType viewType,
        Func<Music, bool> filterPredicate = null)
        {
            var query = AllSongs.AsEnumerable();
            // 2. 应用外部传入的谓词 (如 s => s.Album == CurrentAlbumObj.Album)
            if (filterPredicate != null)            {
                query = query.Where(filterPredicate);
            }
            // 3. 应用搜索过滤 (SearchText)
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = viewType switch
                {
                    SongViewType.Album => query.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)),
                    SongViewType.Artist  => query.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)),
                    SongViewType.Folder => query.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
                    ),
                    _ => query.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                };
               
            }
            // 4. 应用排序逻辑 (参考旧的 UpdateViewSort)
            var tag = SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder";
            if (tag == "DefaultOrder")
            {
                query = viewType switch
                {
                    SongViewType.Album => query.OrderBy(m => m.TrackNumber),
                    SongViewType.Artist or SongViewType.Folder =>
                        query.OrderBy(m => m.Album).ThenBy(m => m.TrackNumber),
                    _ => query.OrderBy(m => m.Title)
                };
            }
            else
            {
                query = tag switch
                {
                    "A-Z" => query.OrderBy(m => m.Title),
                    "Artist" => query.OrderBy(m => m.Author),
                    "Album" => query.OrderBy(m => m.Album),
                    "CreateTimeASC" => query.OrderBy(m => m.CreateTime),
                    "CreateTimeDESC" => query.OrderByDescending(m => m.CreateTime),
                    "UpdateTimeASC" => query.OrderBy(m => m.UpdateTime),
                    "UpdateTimeDESC" => query.OrderByDescending(m => m.UpdateTime),
                    _ => query.OrderBy(m => m.Title)
                };
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                _ = targetCollection.ReplaceAllAsync(query);
            });
        }

        public void UpdateGroupedByFirstLetter(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, ObservableCollection<GenericGroup> source)
        {
            IEnumerable<Music> filteredSource = AllSongs;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                filteredSource = AllSongs.Where(m =>
                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            // 2. 对去重后的专辑进行首字母分组
            var groups = filteredSource
                .GroupBy(distinctSelector)
                .Select(g => g.First())
                .GroupBy(groupSelector)
                .Select(g => new GenericGroup
                {
                    Key = g.Key,
                    // 这里的 Items 现在是每个专辑只有一个代表对象
                    Items = new ObservableCollection<Music>(g.OrderBy(distinctSelector))
                })
                .OrderBy(g => g.Key == "ZZZ" ? "#" : g.Key)
                .ToList();
            source.Clear();
            foreach (var group in groups)
            {
                source.Add(group);
            }
        }


        //private void UpdateGroupedByFirstLetterSort(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, ObservableCollection<GenericGroup> source)
        //{
        //    try
        //    {
        //        var filteredSource = string.IsNullOrWhiteSpace(SearchText)
        //                                ? AllSongs
        //                                : AllSongs.Where(m =>
        //                                    (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
        //                                    (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));

        //        // 2. 去重 (每个专辑/艺术家取一个代表)
        //        var distinctItems = filteredSource
        //            .GroupBy(distinctSelector)
        //            .Select(g => g.First());

        //        // 3. 执行全局排序 (只对去重后的项排序)
        //        IEnumerable<Music> sortedItems = distinctItems;

        //        if (SelectedSortOption.Tag != "DefaultOrder")
        //        {
        //            Func<Music, object> keySelector = SelectedSortOption.Tag switch
        //            {
        //                "A-Z" => m => m.Title,
        //                "Artist" => m => m.Author,
        //                "Album" => m => m.Album,
        //                "CreateTimeASC" or "CreateTimeDESC" => m => m.CreateTime,
        //                "UpdateTimeDESC" or "UpdateTimeASC" => m => m.UpdateTime,
        //                _ => m => distinctSelector(m)
        //            };

        //            bool isAscending = !SelectedSortOption.Tag.EndsWith("DESC");

        //            sortedItems = isAscending
        //                ? distinctItems.OrderBy(keySelector)
        //                : distinctItems.OrderByDescending(keySelector);
        //        }

        //        // 4. 对排好序的项进行首字母分组
        //        // 注意：GroupBy 会保持原有序列的顺序（即保持 sortedItems 的顺序）
        //        var groups = sortedItems
        //            .GroupBy(groupSelector)
        //            .Select(g => new GenericGroup
        //            {
        //                Key = g.Key,
        //                Items = new ObservableCollection<Music>(g) // 这里的顺序已经排好了
        //            })
        //            // 5. 组间排序（首字母索引 A-Z 永远升序，#在后）
        //            .OrderBy(g => g.Key == "ZZZ" ? "#"  : g.Key)
        //            .ToList();

        //        // 6. 更新视图数据源
        //        source.Clear();
        //        foreach (var group in groups)
        //        {
        //            source.Add(group);
        //        }
        //    }
        //    catch { }
        //}

        //public async Task AddToFavourite(Music music)
        //{
        //    music.IsFavorite = !music.IsFavorite;
        //    await _musicDatabaseService.AddToFavourite(music);
        //    if (CurrentPlayingMusic?.Id == music.Id)
        //    {
        //        CurrentPlayingMusic.IsFavorite = music.IsFavorite;
        //    }
        //}

        //public void AddToCurrentPlayList(IEnumerable<Music> uniqueSelectedMusics)
        //{
        //    int index = GetCurrentIndex();

        //    if (index != -1 && uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
        //    {
        //        var existingIds = new HashSet<int>(CurrentPlayingList.AsValueEnumerable().Select(m => m.Id).ToArray());
        //        var newMusicsToAdd = uniqueSelectedMusics.AsValueEnumerable()
        //            .Where(music => !existingIds.Contains(music.Id)).ToList();
        //        for (int i = newMusicsToAdd.Count - 1; i >= 0; i--)
        //        {
        //            CurrentPlayingList.Insert(index + 1, newMusicsToAdd[i]);
        //        }
        //    }
        //}

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
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    RefreshDataSource();
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        private void RefreshDataSource() {
            RefreshFavoriteMapping();
            AllSongsView.RefreshFilter();
            _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            RefreshPlayListSongMapping();
            UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            UpdateGroupedByFirstLetter(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
            App.Services.GetRequiredService<MusicBrowsePage>().UpdateViewList();
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
                "UpdateTimeASC" => query.OrderBy(m => m.UpdateTime),
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

        public void RefreshPlayListSongMapping()
        {
            // 1. 基础 Join 查询，生成包装器序列
            var query = AppData.allPlayListMusics
                        .Where(plm => plm.PlayListId == CurrentPlayListId)
                        .Join(
                            AllSongs,
                            plm => plm.MusicId,
                            m => m.Id,
                            (plm, m) => new PlayListMusicItem
                            {
                                Music = m,                // 保持引用
                                PlayListOrder = plm.Order  // 记录歌单顺序
                            }
                        );

            // 2. 搜索过滤 (注意通过 m.Music 访问属性)
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(m =>
                    (m.Music.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Music.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Music.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            // 3. 排序逻辑 (修正类型为 IEnumerable<PlayListMusicItem>)
            IEnumerable<PlayListMusicItem> sortedQuery = SelectedSortOption?.Tag switch
            {
                "A-Z" => query.OrderBy(m => m.Music.Title),
                "Artist" => query.OrderBy(m => m.Music.Author),
                "Album" => query.OrderBy(m => m.Music.Album),
                "CreateTimeASC" => query.OrderBy(m => m.Music.CreateTime),
                "CreateTimeDESC" => query.OrderByDescending(m => m.Music.CreateTime),
                "UpdateTimeDESC" => query.OrderByDescending(m => m.Music.UpdateTime),
                "DefaultOrder" => query.OrderByDescending(m => m.PlayListOrder),
                _ => query.OrderByDescending(m => m.PlayListOrder)
            };

            // 4. 更新集合
            var results = sortedQuery.ToList();

            // 建议：在 UI 线程操作 ObservableCollection
            PlayListSongs.Clear();
            foreach (var item in results)
            {
                PlayListSongs.Add(item);
            }
        }

        public void OnSelectSortChanged()
        {
            if (SelectedSortOption == null) return;
            UpdateViewSort(AllSongsView, SongViewType.All);
            _ = UpdateSongCollectionsAsync(AlbumSongs,SongViewType.Album,m=>m.Album == CurrentAlbumObj?.Album);
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            UpdateCollectionSort(FavoriteSongs);
            UpdatePlayListCollectionSort(PlayListSongs);
            //UpdateGroupedByFirstLetterSort(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            //UpdateGroupedByFirstLetterSort(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            //UpdateGroupedByFirstLetterSort(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
        }

        public enum SongViewType
        {
            All,
            Album,
            Artist,
            Folder
        }

        private void UpdateViewSort(AdvancedCollectionView view, SongViewType viewType)
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();

                // 获取用户选择的基础排序方案
                var tag = SelectedSortOption.Tag?.ToString();

                // 逻辑拆分：如果是“默认排序”，根据视图类型走差异化逻辑
                if (tag == "DefaultOrder")
                {
                    ApplyDefaultSort(view, viewType);
                }
                else
                {
                    // 其他通用排序逻辑
                    var (propertyName, direction) = tag switch
                    {
                        "A-Z" => (nameof(Music.Title), SortDirection.Ascending),
                        "Artist" => (nameof(Music.Author), SortDirection.Ascending),
                        "Album" => (nameof(Music.Album), SortDirection.Ascending),
                        "CreateTimeASC" => (nameof(Music.CreateTime), SortDirection.Ascending),
                        "CreateTimeDESC" => (nameof(Music.CreateTime), SortDirection.Descending),
                        "UpdateTimeASC" => (nameof(Music.UpdateTime), SortDirection.Ascending),
                        "UpdateTimeDESC" => (nameof(Music.UpdateTime), SortDirection.Descending),
                        _ => (nameof(Music.Title), SortDirection.Ascending)
                    };
                    view.SortDescriptions.Add(new SortDescription(propertyName, direction));
                }
            }
        }

        // 3. 处理差异化的默认排序（无反射）
        private void ApplyDefaultSort(AdvancedCollectionView view, SongViewType viewType)
        {
            switch (viewType)
            {
                case SongViewType.Album:
                    // 专辑视图：按轨道号排序
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.TrackNumber), SortDirection.Ascending));
                    break;
                case SongViewType.Artist:
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.Album), SortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.TrackNumber), SortDirection.Ascending));
                    break;
                case SongViewType.Folder:
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.Album), SortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.TrackNumber), SortDirection.Ascending));
                    break;
                default:
                    view.SortDescriptions.Add(new SortDescription(nameof(Music.Title), SortDirection.Ascending));
                    break;
            }
        }

        private void UpdateCollectionSort(ObservableCollection<Music> collection)
        {
            if (collection == null || !collection.Any()) return;

            // 1. 定义排序键提取器 (直接指向 Music 属性)
            Func<Music, object> keySelector = SelectedSortOption.Tag switch
            {
                "A-Z" => m => m.Title,
                "Artist" => m => m.Author,
                "Album" => m => m.Album,
                "CreateTimeASC" => m => m.CreateTime,
                "CreateTimeDESC" => m => m.CreateTime,
                "UpdateTimeASC" => m => m.UpdateTime,
                "UpdateTimeDESC" => m => m.UpdateTime,
                "DefaultOrder" => m => m.Order,
                _ => m => m.Title
            };

            // 2. 统一判断升降序
            bool isAscending = SelectedSortOption.Tag switch
            {
                "CreateTimeDESC" or "UpdateTimeDESC" or "DefaultOrder" => false,
                _ => true
            };

            // 3. 执行排序
            var sortedList = isAscending
                ? collection.OrderBy(keySelector).ToList()
                : collection.OrderByDescending(keySelector).ToList();

            // 4. 更新原集合 (触发 UI 刷新)
            collection.Clear();
            foreach (var item in sortedList)
            {
                collection.Add(item);
            }
        }

        private void UpdatePlayListCollectionSort(ObservableCollection<PlayListMusicItem> collection)
        {
            if (collection == null || !collection.Any()) return;

            // 1. 定义排序键提取器 (Key Selector)
            // 使用 Func<PlayListMusicItem, object> 统一处理不同类型的排序字段
            Func<PlayListMusicItem, object> keySelector = SelectedSortOption.Tag switch
            {
                "A-Z" => item => item.Music?.Title,
                "Artist" => item => item.Music?.Author,
                "Album" => item => item.Music?.Album,
                "CreateTimeASC" => item => item.Music?.CreateTime,
                "CreateTimeDESC" => item => item.Music?.CreateTime,
                "UpdateTimeASC" => item => item.Music?.UpdateTime,
                "UpdateTimeDESC" => item => item.Music?.UpdateTime,
                "DefaultOrder" => item => item.PlayListOrder,
                _ => item => item.PlayListOrder
            };

            // 2. 确定升降序
            bool isAscending = SelectedSortOption.Tag switch
            {
                "CreateTimeDESC" or "UpdateTimeDESC" or "DefaultOrder" => false,
                _ => true
            };

            // 3. 执行排序并更新集合
            var sortedList = isAscending
                ? collection.OrderBy(keySelector).ToList()
                : collection.OrderByDescending(keySelector).ToList();

            // 4. 回填集合 (保持对同一个 ObservableCollection 的引用，以便 UI 刷新)
            collection.Clear();
            foreach (var item in sortedList)
            {
                collection.Add(item);
            }
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
            FavoriteSongs.Remove(music);
        }

        public void RemoveFromAllSongs(Music music)
        {
            AllSongs.Remove(music);
        }

        public void RemoveFromPlayListSongs(Music music)
        {
            if (music == null) return;
            var itemToRemove = PlayListSongs.FirstOrDefault(item => item.Music == music);
            if (itemToRemove != null)
            {
                PlayListSongs.Remove(itemToRemove);
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

        public async Task ReGetLyrics(IEnumerable<Music> uniqueSelectedMusics,Music? selectedMusic = null)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 0)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
                    //Music? music = AllSongs.AsValueEnumerable().Where(m => m.Id == item.Id).FirstOrDefault();
                    item.Lyrics = lyrics;
                    item.TranslatdeLyrics = transLrc;
                    await _musicDatabaseService.UpdateMusicInfo(item);
                }
            }
            //else
            //{
            //    if (selectedMusic is null) return;
            //    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(selectedMusic);
            //    Music? music = AllSongs.AsValueEnumerable().Where(m => m.Id == selectedMusic.Id).FirstOrDefault();
            //    if (music is not null)
            //    {
            //        music.Lyrics = lyrics;
            //        music.TranslatdeLyrics = transLrc;
            //        await _musicDatabaseService.UpdateMusicInfo(music);
            //    }
            //}
        }

        public async void EditPlayListName(PlayList playList, Func<Task<string>> getNameCallback)
        {
            if (playList is null || getNameCallback is null) return;

            string newName = await getNameCallback();

            if (!string.IsNullOrEmpty(newName))
            {
                playList.Name = newName;
                await _musicDatabaseService.UpdatePlayList(playList);
                UpdateMenuOptionsPlayList();
            }
        }

        public async Task RescanFolder(Music music)
        {
            try
            {
                if (music is not null)
                {
                    if (!string.IsNullOrEmpty(music.FolderPath))
                    {
                        await Task.Run(async () =>
                        {
                            await App.Services.GetRequiredService<MusicDatabaseService>().RescanFolderByPath(music.FolderPath);
                        });
                    }
                }
            }
            catch {
            }
        }
    }
}