using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using WinUIMusicPlayer.Behaviors;
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
    public partial class AppViewModel : ObservableObject, IDisposable
    {
        private int _loadingMusicId;
        private int _lastDisplayedSecond = -1;
        public Music? CurrentArtistObj
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == value?.Author);
                }
            }
        }
        public Music? CurrentAlbumObj
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == value?.Album);
                }
            }
        }
        public Music? CurrentFolderObj
        {
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
        private CancellationTokenSource? SearchCts { get; set; }
        public BulkObservableCollection<Music> SongsSource { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
        public PlayList CurrentPlayList { get; set => SetProperty(ref field, value); }
        public int CurrentPlayListId { get; set => SetProperty(ref field, value); }
        public CollectionViewSource AlbumPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource ArtistPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource FolderPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public BulkObservableCollection<Music> ListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> AlbumSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> ArtistSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FolderSongs { get; set => SetProperty(ref field, value); } = [];
        public Music CurrentPlayingMusic
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value) && IsInitialized)
                {
                    WeakReferenceMessenger.Default.Send(new OffsetMsMessage(value?.LyricsOffsetMs ?? 0));
                }
            }
        }
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
                    else
                    {
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
        public bool IsMuted { get; set => SetProperty(ref field, value); } = false;
        public double TempVolume { get; set => SetProperty(ref field, value); } = 50;
        public string PlayTimeText { get; set => SetProperty(ref field, value); } = "00:00/00:00";
        public double ProgressSliderMax { get; set => SetProperty(ref field, value); } = 100;
        public List<LyricLine> UILyrics
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                    WeakReferenceMessenger.Default.Send(new UILyricsMessage(value));
            }
        } = [];
        public int LastLyricIndex { get; set => SetProperty(ref field, value); } = -1;
        public byte[] LyricPageBackgroundData { get; set => SetProperty(ref field, value); } = [];
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
        public float DpiValue { get; set => SetProperty(ref field, value); } = 1.0f;
        public float TopControlsOpacity { get; set => SetProperty(ref field, value); } = 1.0f;
        public bool IsBackBtnEnable { get; set => SetProperty(ref field, value); } = false;
        public TimeSpan LyricsDurationTime { get; set; } = TimeSpan.Zero;
        public bool IsManualSelect { get; set; } = false;
        public bool IsMouseOverVolumeSlider { get; set; } = false;
        private System.Timers.Timer ProgressTimer { get; set; }
        private TimeSpan TotalTime { get; set; }
        private TimeSpan CurrentTime { get; set; }
        public TimeSpan CurrentPlayingTime { get; set => SetProperty(ref field, value); } = TimeSpan.Zero;
        private StringBuilder TimeStringBuilder { get; set; } = new StringBuilder(16);
        private SystemMediaControlsService SystemMediaControlsService { get; set; }

        // 带有复杂逻辑的属性重构
        public bool UseImageDominantTheme
        {
            get => field; set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public bool IsFluidBackgroundEnabled
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
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
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

        public bool IsPlaying
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppData.IsPlaying = value;
                    UpdatePlayPauseButtonIcon();
                    WeakReferenceMessenger.Default.Send(new IsPlayingMessage(value));
                }
            }
        } = false;

        public bool IsMouseOverProgressBar
        {
            get;
            set => SetProperty(ref field, value);
        } = false;

        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<AppViewModel> _logger;

        public AppViewModel(MusicDatabaseService musicDatabaseService, SystemMediaControlsService systemMediaControlsService, ILogger<AppViewModel> logger)
        {
            _musicDatabaseService = musicDatabaseService;
            SystemMediaControlsService = systemMediaControlsService;
            _logger = logger;
            AllPlayList.CollectionChanged += AllPlayList_CollectionChanged;
            SongsSource.CollectionChanged += SongsSource_CollectionChanged;
            ProgressTimer = new System.Timers.Timer(200);
            ProgressTimer.Elapsed += ProgressTimer_Elapsed;
            WeakReferenceMessenger.Default.Register<RequestLyricsSettingsMessage>(this, (r, m) => SendFullLyricsSync());
        }

        public void UpdatePlayPauseButtonIcon()
        {
            App.MainWindow.UpdateTaskbarIcon();
            SystemMediaControlsService.UpdateSystemMediaControlsState();
        }
        private async void HandleProgressSliderChange(double value)
        {
            if (IsMouseOverProgressBar)
            {
                if (!IsUserDraggingProgressSlider)
                {
                    var (curMs, _) = await App.Services.GetRequiredService<BassPlayerCommandService>().GetTimeProgress();
                    double currentPlayPosition = curMs / 1000.0;
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
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public async void UpdateProgressTimerUI()
        {
            try
            {
                var (curMs, totalMs) = await App.Services.GetRequiredService<BassPlayerCommandService>().GetTimeProgress();
                TotalTime = TimeSpan.FromMilliseconds(totalMs);
                CurrentTime = TimeSpan.FromMilliseconds(curMs);
                double currentTimeMs = curMs;
                int currentSecond = (int)CurrentTime.TotalSeconds;
                bool secondChanged = currentSecond != _lastDisplayedSecond;
                if (secondChanged)
                    _lastDisplayedSecond = currentSecond;

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    CurrentPlayingTime = CurrentTime;
                    WeakReferenceMessenger.Default.Send(new CurrentPlayingTimeMessage(currentTimeMs));
                    if (!IsManualSelect)
                    {
                        try
                        {
                            ProgressSlider = curMs / 1000.0;
                            ProgressSliderMax = totalMs / 1000.0;
                            if (secondChanged)
                            {
                                TimeStringBuilder.Clear();
                                if (TotalTime.TotalHours >= 1)
                                {
                                    PlayTimeText = TimeStringBuilder
                                        .Append(CurrentTime.Hours.ToString("D2"))
                                        .Append(':')
                                        .Append(CurrentTime.Minutes.ToString("D2"))
                                        .Append(':')
                                        .Append(CurrentTime.Seconds.ToString("D2"))
                                        .Append('/')
                                        .Append(TotalTime.Hours.ToString("D2"))
                                        .Append(':')
                                        .Append(TotalTime.Minutes.ToString("D2"))
                                        .Append(':')
                                        .Append(TotalTime.Seconds.ToString("D2"))
                                        .ToString();
                                }
                                else
                                {
                                    PlayTimeText = TimeStringBuilder
                                        .Append(CurrentTime.Minutes.ToString("D2"))
                                        .Append(':')
                                        .Append(CurrentTime.Seconds.ToString("D2"))
                                        .Append('/')
                                        .Append(TotalTime.Minutes.ToString("D2"))
                                        .Append(':')
                                        .Append(TotalTime.Seconds.ToString("D2"))
                                        .ToString();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"UpdateProgressTimerUI 更新进度条UI失败: {ex.Message}");
                        }
                    }
                });
                SystemMediaControlsService.UpdateTimelineProperties(CurrentTime, TotalTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public void LoadLyricsToUI(Music music)
        {

            _loadingMusicId = music.Id;
            _ = Task.Run(async () =>
            {
                LastLyricIndex = -1;
                //设置播放服务中的歌词
                List<LyricLine> parsedLyrics = await App.Services.GetRequiredService<LyricsRefreshService>().SetLyrics(music);
                // 解析歌词并添加到UI集合
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_loadingMusicId == music.Id)
                    {
                        UILyrics = parsedLyrics;
                    }
                });
            });

        }

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }

        private void SongsSource_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (IsInitialized)
            {
                RefreshAllViews();
            }
        }

        private void AllPlayList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateMenuOptionsPlayList();
        }

        public void UpdateMenuOptionsPlayList()
        {
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
            Func<Music, bool>? searchPredicate = null;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                searchPredicate = viewType switch
                {
                    SongViewType.Album => m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                    SongViewType.Artist => m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                    SongViewType.Folder => m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.LastLevelFolderPath?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                    _ => m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
                };
            }

            Func<Music, bool>? combinedPredicate = filterPredicate != null && searchPredicate != null
                ? m => filterPredicate(m) && searchPredicate(m)
                : filterPredicate ?? searchPredicate;

            IEnumerable<Music> query = combinedPredicate != null
                ? SongsSource.AsValueEnumerable().Where(combinedPredicate).ToList()
                : SongsSource;

            var tag = SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder";
            if (tag == "DefaultOrder")
            {
                query = viewType switch
                {
                    SongViewType.Album => query.OrderBy(m => m.DiskNumber).ThenBy(m => m.TrackNumber),
                    SongViewType.Artist or SongViewType.Folder =>
                        query.OrderBy(m => m.Album).ThenBy(m => m.DiskNumber).ThenBy(m => m.TrackNumber),
                    SongViewType.Favorite => query.OrderByDescending(m => m.Order),
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
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _ = targetCollection.ReplaceAllAsync(query);
            });
        }

        public void UpdateGroupedByFirstLetter(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
        {
            try
            {
                IEnumerable<Music> filteredSource = string.IsNullOrWhiteSpace(SearchText)
                    ? SongsSource
                    : SongsSource.AsValueEnumerable().Where(m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.LastLevelFolderPath?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

                var distinctItems = filteredSource
                    .AsValueEnumerable()
                    .GroupBy(distinctSelector)
                    .Select(g => g.First())
                    .ToList();

                var sortedItems = SelectedSortOption.Tag switch
                {
                    "A-Z" => distinctItems.OrderBy(m => m.Title),
                    "Artist" => distinctItems.OrderBy(m => m.Author),
                    "Album" => distinctItems.OrderBy(m => m.Album),
                    "CreateTimeASC" => distinctItems.OrderBy(m => m.CreateTime),
                    "CreateTimeDESC" => distinctItems.OrderByDescending(m => m.CreateTime),
                    "UpdateTimeASC" => distinctItems.OrderBy(m => m.UpdateTime),
                    "UpdateTimeDESC" => distinctItems.OrderByDescending(m => m.UpdateTime),
                    "DefaultOrder" => distinctItems.OrderBy(distinctSelector),
                    _ => distinctItems.OrderBy(distinctSelector)
                };

                var groups = sortedItems
                    .AsValueEnumerable()
                    .GroupBy(groupSelector)
                    .Select(g => new MusicGroup(g.Key, g))
                    .OrderBy(g => g.Key == "ZZZ" ? "#" : g.Key)
                    .ToList();

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    source.Source = groups;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
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
            SearchCts?.Cancel();
            SearchCts?.Dispose();
            SearchCts = null;
            SearchCts = new CancellationTokenSource();
            var token = SearchCts.Token;
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

        public void RefreshDataSource()
        {
            RefreshDataForPageType(AppData.CurrentPage);
        }

        public void RefreshAllViews()
        {
            _ = UpdateSongCollectionsAsync(FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
            _ = UpdateSongCollectionsAsync(ListSongs, SongViewType.All);
            _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            RefreshPlayListSongMapping();
            UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            UpdateGroupedByFirstLetter(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
            App.Services.GetRequiredService<MusicBrowseViewModel>()?.UpdateViewList();
        }

        private void RefreshDataForPageType(Type pageType)
        {
            if (pageType == typeof(SongListPage))
            {
                _ = UpdateSongCollectionsAsync(ListSongs, SongViewType.All);
            }
            else if (pageType == typeof(SongCollectionPage))
            {
                _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
            }
            else if (pageType == typeof(AlbumPage))
            {
                _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
                UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            }
            else if (pageType == typeof(SongArtistListPage))
            {
                _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
            }
            else if (pageType == typeof(ArtistPage))
            {
                _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => m.Author == CurrentArtistObj?.Author);
                UpdateGroupedByFirstLetter(m => m.Author, m => GetFirstLetterAdvanced(m.Author), ArtistPageSource);
            }
            else if (pageType == typeof(SongFolderListPage))
            {
                _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            }
            else if (pageType == typeof(FolderBrowsePage))
            {
                _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
                UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
            }
            else if (pageType == typeof(FavouritePlayListPage))
            {
                _ = UpdateSongCollectionsAsync(FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
            }
            else if (pageType == typeof(PlayListSongPage) || pageType == typeof(PlayListPage))
            {
                RefreshPlayListSongMapping();
            }
            else
            {
                return;
            }
            App.Services.GetRequiredService<MusicBrowseViewModel>()?.UpdateViewList();
        }

        public void RefreshPlayListSongMapping()
        {
            IEnumerable<PlayListMusicItem> query;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = AppData.AllPlayListMusics.AsValueEnumerable()
                    .Where(plm => plm.PlayListId == CurrentPlayListId)
                    .Join(SongsSource, plm => plm.MusicId, m => m.Id,
                        (plm, m) => new PlayListMusicItem { Music = m, PlayListOrder = plm.Order })
                    .Where(item =>
                        (item.Music.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Music.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Music.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
            }
            else
            {
                query = AppData.AllPlayListMusics.AsValueEnumerable()
                    .Where(plm => plm.PlayListId == CurrentPlayListId)
                    .Join(SongsSource, plm => plm.MusicId, m => m.Id,
                        (plm, m) => new PlayListMusicItem { Music = m, PlayListOrder = plm.Order })
                    .ToList();
            }

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

            var results = sortedQuery.ToList();

            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _ = PlayListSongs.ReplaceAllAsync(results);
            });
        }

        public void OnSelectSortChanged()
        {
            RefreshDataSource();
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
            if (collection == null || !collection.AsValueEnumerable().Any()) return;

            var tag = SelectedSortOption.Tag;
            var sortedList = tag switch
            {
                "A-Z" => collection.AsValueEnumerable().OrderBy(item => item.Music?.Title).ToList(),
                "Artist" => collection.AsValueEnumerable().OrderBy(item => item.Music?.Author).ToList(),
                "Album" => collection.AsValueEnumerable().OrderBy(item => item.Music?.Album).ToList(),
                "CreateTimeASC" => collection.AsValueEnumerable().OrderBy(item => item.Music?.CreateTime).ToList(),
                "CreateTimeDESC" => collection.AsValueEnumerable().OrderByDescending(item => item.Music?.CreateTime).ToList(),
                "UpdateTimeASC" => collection.AsValueEnumerable().OrderBy(item => item.Music?.UpdateTime).ToList(),
                "UpdateTimeDESC" => collection.AsValueEnumerable().OrderByDescending(item => item.Music?.UpdateTime).ToList(),
                "DefaultOrder" => collection.AsValueEnumerable().OrderByDescending(item => item.PlayListOrder).ToList(),
                _ => collection.AsValueEnumerable().OrderByDescending(item => item.PlayListOrder).ToList()
            };

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

        public void RefreshSongsSource()
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                _ = SongsSource.ReplaceAllAsync(await _musicDatabaseService.GetMusicListAsync());
            });
        }

        public void RemoveFromSongsSource(Music music)
        {
            SongsSource.Remove(music);
        }

        public void RemoveFromPlayListSongs(Music music)
        {
            if (music == null) return;
            var itemToRemove = PlayListSongs.AsValueEnumerable().FirstOrDefault(item => item.Music == music);
            if (itemToRemove != null)
            {
                PlayListSongs.Remove(itemToRemove);
            }
        }

        public void UpDateUsbDeviceMenuflyout()
        {
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
                foreach (var music in SongsSource)
                {
                    music.IsExistOnDevice = 0;
                }
            });
        }

        public void RefreshUsbDeviceMusicList()
        {
            var usbMusicGroups = AppData.MusicOnUsbDevice.AsValueEnumerable()
                            .GroupBy(u => u.Title)
                            .ToDictionary(g => g.Key, g => g.AsValueEnumerable().ToList());
            foreach (var music in SongsSource)
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

        public async Task ReGetLyrics(IEnumerable<Music> uniqueSelectedMusics, Music? selectedMusic = null)
        {
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Count() > 0)
            {
                foreach (Music item in uniqueSelectedMusics)
                {
                    (string lyrics, string transLrc) = await ToolUtils.GetLyricsFromNet(item);
                    (string krc, string tKrc) = await ToolUtils.GetKrcFromNet(item);
                    await _musicDatabaseService.SaveLyricsAsync(item.Id, lyrics, transLrc, krc ?? "", tKrc ?? "");
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
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RescanFolder 重新扫描文件夹失败: {ex.Message}");
            }
        }

        public async Task TransmitFileToUsb(IEnumerable<Music> selectedMusics, UsbStorageDevice usbDevice)
        {
            if (selectedMusics.AsValueEnumerable().Any())
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().ShowTransmission();
                using (var usbWriter = new UsbWriterHelper())
                {
                    usbWriter.hideTransmission += (sender, args) =>
                    {
                        App.Services.GetRequiredService<MusicBrowseViewModel>().HideTransmission();
                    };
                    await usbWriter.WriteToUsb(selectedMusics, usbDevice);
                }
                foreach (var music in selectedMusics)
                {
                    var existingMusic = AppData.MusicOnUsbDevice.AsValueEnumerable().Where(m => m.Title == music.Title).FirstOrDefault();
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
                        UniqueDeviceId = AppData.UsbStorageDevice.UniqueId
                    };
                    AppData.MusicOnUsbDevice.Add(usbDeviceMusic);
                }
            }
            RefreshUsbDeviceMusicList();
        }

        public void UpdateCover()
        {
            IsDarkMode = ThemeType switch
            {
                "Default" => !GetIsLightTheme(),
                "Dark" => true,
                "Light" => false,
                _ => !GetIsLightTheme(),
            };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private DispatcherQueueTimer? _settingsDebounceTimer;

        private void ScheduleSettingsBroadcast()
        {
            if (_settingsDebounceTimer is null)
            {
                _settingsDebounceTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                _settingsDebounceTimer.Interval = TimeSpan.FromMilliseconds(1000);
                _settingsDebounceTimer.Tick += (s, e) =>
                {
                    _settingsDebounceTimer?.Stop();
                    SendLyricsSettings();
                };
            }
            _settingsDebounceTimer.Start();
        }

        public void SendLyricsSettings()
        {

            string fontFamilyName = FontFamily?.FontFamily?.Source ?? "Segoe UI";
            var alignment = LyricsAlignment switch
            {
                "Center" => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
                "Right" => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Right,
                _ => Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
            };
            WeakReferenceMessenger.Default.Send(new LyricsSettingsSyncMessage(
                FontFamilyName: fontFamilyName,
                LyricsTextAlignment: alignment,
                IsDark: IsDarkMode,
                ScrollSensitivity: 1.0,
                LyricsBlurAmount: LyricsBlurAmount,
                GlowAmount: GlowAmount,
                CharFloatAmount: CharFloatAmount,
                CharScaleAmount: CharScaleAmount,
                LongSyllableThreshold: LongSyllableThreshold,
                IsFadeOutEnabled: true,
                IsOutOfSightEnabled: true,
                UnplayedOpacity: UnplayedOpacityPercent / 100.0,
                TranslatedOpacity: TranslatedOpacityPercent / 100.0,
                StrokeWidth: 0.0,
                ScrollEasingType: AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2.EasingType.Sine,
                ScrollEasingMode: AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2.EaseMode.Out,
                PlayingLineTopOffset: PlayingLineTopOffsetPercent / 100.0,
                TargetFrameRate: TargetFrameRate));
        }

        private void SendLyricsFontSize()
        {
            double fontSize = IsGlobalFontSizeEnabled ? GlobalFontSize : LyricsFontSize;
            WeakReferenceMessenger.Default.Send(new LyricsFontSizeMessage(fontSize));
        }

        private void SendFullLyricsSync()
        {
            _settingsDebounceTimer?.Stop();
            SendLyricsSettings();
            SendLyricsFontSize();
            WeakReferenceMessenger.Default.Send(new IsPlayingMessage(IsPlaying));
            WeakReferenceMessenger.Default.Send(new CurrentPlayingTimeMessage(CurrentTime.TotalMilliseconds));
            WeakReferenceMessenger.Default.Send(new OffsetMsMessage(CurrentPlayingMusic?.LyricsOffsetMs ?? 0));
            if (UILyrics.Count > 0)
                WeakReferenceMessenger.Default.Send(new UILyricsMessage(UILyrics));
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                ProgressTimer?.Stop();
                SearchCts?.Cancel();
                SearchCts?.Dispose();
                ProgressTimer?.Dispose();
            }
        }
    }
}