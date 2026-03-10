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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using TagLib.Ape;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel : ObservableObject
    {
        public Music? CurrentArtistObj {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
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
        public BulkObservableCollection<Music> AllSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];        
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
        public PlayList CurrentPlayList { get; set => SetProperty(ref field, value); }
        public int CurrentPlayListId { get; set => SetProperty(ref field, value); }
        public CollectionViewSource AlbumPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true};
        public CollectionViewSource ArtistPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource FolderPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public BulkObservableCollection<Music> ListSongs { get; set => SetProperty(ref field, value); } = [];
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
        //public bool IsInPlayingDetailMode { get; set => SetProperty(ref field, value); } = false;
        public bool IsInNaviView { get; set => SetProperty(ref field, value); } = false;
        public float TopControlsOpacity { get; set => SetProperty(ref field, value); } = 1.0f;
        public bool IsBackBtnEnable { get; set => SetProperty(ref field, value); } = false;
        public TimeSpan LyricsDurationTime { get; set; } = TimeSpan.Zero;
        public bool IsManualSelect { get; set; } = false;
        public bool IsMouseOverVolumeSlider { get; set; } = false;
        private System.Timers.Timer ProgressTimer { get; set; }
        private TimeSpan TotalTime { get; set; }
        private TimeSpan CurrentTime { get; set; }
        private StringBuilder TimeStringBuilder { get; set; } = new StringBuilder(16);        
        private SystemMediaControlsService SystemMediaControlsService { get; set; }

        // 带有复杂逻辑的属性重构
        public bool IsCoverCacheEnabled
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
        }
        public bool IsBackgroundCoverEnabled
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
        }
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

        public bool IsUserDraggingProgressSlider
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                }
            }
        } = false;

        public double ProgressSlider
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    HandleProgressSliderChange(value);
                }
            }
        } = 0;

        private async void HandleProgressSliderChange(double value)
        {
            if (IsMouseOverProgressBar)
            {
                if (!IsUserDraggingProgressSlider)
                {
                    double currentPlayPosition = await App.Services.GetRequiredService<BassPlayerCommandService>().GetCurrentPosition();
                    if (Math.Abs(value - currentPlayPosition) > 2.0)
                    {
                        _ = Task.Run(() =>
                        {
                            IsManualSelect = true;
                            App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(TimeSpan.FromSeconds(value));
                            IsManualSelect = false;
                        });
                    }
                }
            }
        }

        public bool IsPlaying
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    App.Services.GetRequiredService<PlayingDetailPage>().BeginOrPauseLyricImgAnimation(value);
                }
            }
        } = false;

        public bool IsMouseOverProgressBar
        {
            get;
            set => SetProperty(ref field, value);
        } = false;

        private MusicDatabaseService _musicDatabaseService { get; }

        public AppViewModel(MusicDatabaseService musicDatabaseService,SystemMediaControlsService systemMediaControlsService)
        {
            _musicDatabaseService = musicDatabaseService;  
            SystemMediaControlsService = systemMediaControlsService;
            AllPlayList.CollectionChanged += AllPlayList_CollectionChanged;
            AllSongs.CollectionChanged += AllSongs_CollectionChanged;
            ProgressTimer = new System.Timers.Timer(200);
            ProgressTimer.Elapsed += ProgressTimer_Elapsed;
        }

        public void StartProgressTimer()
        {
            ProgressTimer?.Start();
        }
        public void StopProgressTimer()
        {
            ProgressTimer?.Stop();
        }

        private void ProgressTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                if (!IsUserDraggingProgressSlider)
                {
                    UpdateProgressTimerUI();
                }
            }
            catch (Exception)
            {
            }
        }

        public async void UpdateProgressTimerUI()
        {
            try
            {
                TotalTime = TimeSpan.FromSeconds(await App.Services.GetRequiredService<BassPlayerCommandService>().GetTotalPosition());
                CurrentTime = TimeSpan.FromSeconds(await App.Services.GetRequiredService<BassPlayerCommandService>().GetCurrentPosition());
                TimeStringBuilder.Clear();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsManualSelect)
                    {
                        try
                        {
                            ProgressSlider = CurrentTime.TotalSeconds;
                            ProgressSliderMax = TotalTime.TotalSeconds;
                            if (TotalTime.TotalHours >= 1)
                            {
                                PlayTimeText = TimeStringBuilder
                                    .AppendFormat("{0:hh\\:mm\\:ss}/{1:hh\\:mm\\:ss}", CurrentTime, TotalTime)
                                    .ToString();
                            }
                            else
                            {
                                PlayTimeText = TimeStringBuilder
                                    .AppendFormat("{0:mm\\:ss}/{1:mm\\:ss}", CurrentTime, TotalTime)
                                    .ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
                    }
                });
                App.Services.GetRequiredService<LyricsRefreshService>().UpdateLyrics(CurrentTime);
                SystemMediaControlsService.UpdateTimelineProperties(CurrentTime, TotalTime);
            }
            catch
            {
            }
        }

        public async void LoadLyricsToUI()
        {
            LastLyricIndex = -1;
            UILyrics.Clear();
            // 设置播放服务中的歌词
            await App.Services.GetRequiredService<LyricsRefreshService>()?.SetLyrics();
            // 解析歌词并添加到UI集合
            List<LyricLine> parsedLyrics = App.Services.GetRequiredService<LyricsRefreshService>().Lyrics;
            foreach (var lyric in parsedLyrics)
            {
                UILyrics.Add(lyric);
            }
        }

        public void UpdateLyricsToUI(int index)
        {
            if (LastLyricIndex == index)
                return;
            TimeSpan duration = TimeSpan.Zero;
            if (index >= 0 && index < UILyrics.Count)
            {
                int nextIndex = index + 1;
                if (nextIndex < UILyrics.Count)
                {
                    TimeSpan currentTime = UILyrics[index].Time;
                    TimeSpan nextTime = UILyrics[nextIndex].Time;
                    LyricsDurationTime = nextTime.Subtract(currentTime);
                }
            }
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    for (int i = 0; i < UILyrics.Count; i++)
                    {
                        UILyrics[i].IsCurrent = (i == index);
                    }
                    LastLyricIndex = index;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新歌词失败: {ex.Message}");
                }
            });
        }

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
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
        Func<Music, bool>? filterPredicate = null)
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
                    SongViewType.Favorite => query.OrderByDescending(m=>m.Order),
                    _ => query.OrderBy(m => m.Title)
                };
            }
            else
            {
                query = tag switch
                {
                    "A-Z" => query.OrderBy(m => m.Title),
                    "Artist" => query.OrderBy(m => m.Author),
                    "Album" => query.OrderBy(m => m.Album).ThenBy(m => m.TrackNumber),
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

        public void UpdateGroupedByFirstLetter(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
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
                .Select(g => new MusicGroup(g.Key, g.OrderBy(distinctSelector)))
                .OrderBy(g => g.Key == "ZZZ" ? "#" : g.Key)
                .ToList();
            App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                source.Source = groups;
            });
        }


        private void UpdateGroupedByFirstLetterSort(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
        {
            try
            {
                var filteredSource = string.IsNullOrWhiteSpace(SearchText)
                                        ? AllSongs
                                        : AllSongs.Where(m =>
                                            (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                            (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));

                // 2. 去重 (每个专辑/艺术家取一个代表)
                var distinctItems = filteredSource
                    .GroupBy(distinctSelector)
                    .Select(g => g.First());

                // 3. 执行全局排序 (只对去重后的项排序)
                IEnumerable<Music> sortedItems = distinctItems;

                Func<Music, object> keySelector = SelectedSortOption.Tag switch
                {
                    "A-Z" => m => m.Title,
                    "Artist" => m => m.Author,
                    "Album" => m => m.Album,
                    "CreateTimeASC" or "CreateTimeDESC" => m => m.CreateTime,
                    "UpdateTimeDESC" or "UpdateTimeASC" => m => m.UpdateTime,
                    "DefaultOrder" => distinctSelector,
                    _ => distinctSelector
                };

                bool isAscending = !SelectedSortOption.Tag.EndsWith("DESC");

                sortedItems = isAscending
                    ? distinctItems.OrderBy(keySelector)
                    : distinctItems.OrderByDescending(keySelector);

                // 4. 对排好序的项进行首字母分组
                // 注意：GroupBy 会保持原有序列的顺序（即保持 sortedItems 的顺序）
                var groups = sortedItems
                    .GroupBy(groupSelector)
                    .Select(g => new MusicGroup(g.Key, g))
                    .OrderBy(g => g.Key == "ZZZ" ? "#" : g.Key)
                    .ToList();

                // 6. 更新视图数据源
                App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                    source.Source = groups;
                });
            }
            catch { }
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

        public void RefreshDataSource() {
            _ = UpdateSongCollectionsAsync(FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
            _ = UpdateSongCollectionsAsync(ListSongs, SongViewType.All);
            _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            RefreshPlayListSongMapping();
            UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            UpdateGroupedByFirstLetter(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
            App.Services.GetRequiredService<MusicBrowsePage>().UpdateViewList();
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
            App.MainWindow.DispatcherQueue.TryEnqueue(() => {
                _ = PlayListSongs.ReplaceAllAsync(query);
            });
        }

        public void OnSelectSortChanged()
        {
            if (SelectedSortOption == null) return;
            _ = UpdateSongCollectionsAsync(FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
            _ = UpdateSongCollectionsAsync(ListSongs, SongViewType.All);
            _ = UpdateSongCollectionsAsync(AlbumSongs,SongViewType.Album,m=>m.Album == CurrentAlbumObj?.Album);
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            UpdatePlayListCollectionSort(PlayListSongs);
            UpdateGroupedByFirstLetterSort(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            UpdateGroupedByFirstLetterSort(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            UpdateGroupedByFirstLetterSort(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
        }

        public enum SongViewType
        {
            All,
            Album,
            Artist,
            Folder,
            Favorite
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

        public void RefreshAllSongs() {
            App.MainWindow.DispatcherQueue.TryEnqueue(async () => {
                _ = AllSongs.ReplaceAllAsync(await _musicDatabaseService.GetMusicListAsync());
            });
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

        public void UpDateUsbDeviceMenuflyout() {
            App.Services.GetRequiredService<FavouritePlayListViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<SongArtistViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<SongListViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<SongCollectionViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<SongFolderListViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<PlayListSongViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<AlbumViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<ArtistViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<FolderViewModel>().UpDateUsbDeviceMenuflyout();
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
                    item.TranslatedLyrics = transLrc;
                    await _musicDatabaseService.UpdateMusicInfo(item);
                }
            }
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

        public async Task TransmitFileToUsb(IEnumerable<Music> selectedMusics, UsbStorageDevice usbDevice)
        {
            if (selectedMusics.Any())
            {
                App.Services.GetRequiredService<MusicBrowsePage>().ShowTransmission();
                using (var usbWriter = new UsbWriterHelper())
                {
                    usbWriter.hideTransmission += (sender, args) =>
                    {
                        App.Services.GetRequiredService<MusicBrowsePage>().HideTransmission();
                    };
                    await usbWriter.WriteToUsb(selectedMusics, usbDevice);
                }
                foreach (var music in selectedMusics)
                {
                    var existingMusic = AppData.musicOnUsbDevice.AsValueEnumerable().Where(m => m.Title == music.Title).FirstOrDefault();
                    if (existingMusic is not null)
                    {
                        continue; // 如果已经存在，则跳过
                    }
                    UsbDeviceMusic usbDeviceMusic = new()
                    {
                        Title = music.Title,
                        Author = music.Author,
                        Album = music.Album,
                        Extension = music.Extension,
                        UniqueDeviceId = AppData.usbStorageDevice.UniqueId
                    };
                    AppData.musicOnUsbDevice.Add(usbDeviceMusic);
                }
            }
            RefreshUsbDeviceMusicList();
        }

        public async Task UpdateCover(byte[] cover)
        {
            try
            {
                if (cover != null)
                {
                    bool isDarkMode = true;
                    if (AppSettings.AppTheme == "Light")
                    {
                        isDarkMode = false;
                    }
                    else if (AppSettings.AppTheme == "Default")
                    {
                        // 注意：GetIsLightTheme() 最好是同步方法
                        isDarkMode = !GetIsLightTheme();
                    }
                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        LyricPageBackgroundSource = await ImageHelper.ApplyMicaEffectWin2DAsync(cover, isDarkMode);
                    });

                }
                else
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        LyricPageBackgroundSource = null;
                    });
                }
            }
            catch
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    LyricPageBackgroundSource = null;
                });
            }
        }

    }
}