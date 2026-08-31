using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel.Controls;
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
                    _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, value?.Author ?? ""));
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

        /// <summary>按枚举名设置播放模式（托盘菜单 CommandParameter 传入），标题文本随 setter 统一同步。</summary>
        [RelayCommand]
        private void SetPlayMode(string mode)
        {
            if (Enum.TryParse(mode, true, out PlayMode playMode))
                CurrentPlayMode = playMode;
        }

        public string SearchText
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (_searchDebounceTimer == null)
                    {
                        _searchDebounceTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                        _searchDebounceTimer.IsRepeating = false;
                        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
                    }
                    _searchDebounceTimer.Start();
                }
            }
        }
        private DispatcherQueueTimer? _searchDebounceTimer;
        private List<Music> _songsSource = [];
        public List<Music> SongsSource
        {
            get => _songsSource;
            set
            {
                if (ReferenceEquals(_songsSource, value)) return;
                _songsSource = value;
                RebuildIdIndex();
            }
        }
        private readonly Dictionary<int, Music> _idIndex = new(capacity: 16384);
        private readonly Dictionary<string, Music> _firstAlbumIndex = new(capacity: 4096, StringComparer.Ordinal);
        private readonly Dictionary<string, Music> _firstArtistIndex = new(capacity: 4096, StringComparer.Ordinal);
        private readonly Dictionary<string, Music> _firstFolderIndex = new(capacity: 4096, StringComparer.Ordinal);
        private readonly Dictionary<string, int> _albumSongCounts = new(capacity: 4096, StringComparer.Ordinal);
        private bool _indexDirty = true;
        public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
        public PlayList CurrentPlayList { get; set => SetProperty(ref field, value); }
        public int CurrentPlayListId { get; set; }
        public CollectionViewSource AlbumPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource ArtistPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource FolderPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public BulkObservableCollection<Music> ListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> AlbumSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> ArtistSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FolderSongs { get; set => SetProperty(ref field, value); } = [];
        public Music? CurrentPlayingMusic
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value) && IsInitialized)
                {
                    AnimatedWin2dControls.Messages.OffsetMsBus.Publish(value?.LyricsOffsetMs ?? 0);
                }
            }
        }
        public BulkObservableCollection<Music> SequentialPlayingList
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
        public BulkObservableCollection<Music> CurrentPlayingList { get; set => SetProperty(ref field, value); } = new();
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
        public bool IsMuted { get; set; } = false;
        public double TempVolume { get; set; } = 50;
        public string PlayTimeText { get; set => SetProperty(ref field, value); } = "00:00/00:00";
        //public string ProgressSliderThumbTipText { get; set => SetProperty(ref field, value); } = "00:00";
        public double ProgressSliderMax { get; set => SetProperty(ref field, value); } = 100;
        public List<LyricLine> UILyrics
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                    AnimatedWin2dControls.Messages.UILyricsBus.Publish(value);
            }
        } = [];
        public int LastLyricIndex { get; set; } = -1;
        public string LyricPageBackgroundHash { get; set => SetProperty(ref field, value); } = "";
        public AnimatedWin2dControls.Impressionist.PaletteResult? LyricPagePalette { get; set => SetProperty(ref field, value); }
        // 当前曲目封面像素（RotatingMesh 背景着色器消费；null 表示无封面，着色器回退调色板渐变）。
        public AnimatedWin2dControls.Impressionist.ArtworkPixelData? LyricPageArtwork { get; set => SetProperty(ref field, value); }
        public bool IsInitialized { get; set; } = false;
        public Visibility UsbDeviceVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public ObservableCollection<UsbStorageDevice> UsbStorageDevices { get; set => SetProperty(ref field, value); }
        public int UsbSelectedIndex { get; set => SetProperty(ref field, value); } = 0;
        public Visibility ProcessRingVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public bool IsFullScreen
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                    ApplyFullScreen(value);
            }
        } = false;
        public bool IsMaximized { get; set => SetProperty(ref field, value); } = false;
        public bool IsPlayingDetailVisible { get; set => SetProperty(ref field, value); } = false;
        public bool IsPointerOverTitleBar { get; set => SetProperty(ref field, value); } = true;

        public void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

        private void ApplyFullScreen(bool enable)
        {
            var window = App.MainWindow?.AppWindow;
            if (window is null) return;

            var desired = enable
                ? AppWindowPresenterKind.FullScreen
                : AppWindowPresenterKind.Default;

            if (window.Presenter.Kind != desired)
                window.SetPresenter(desired);
        }

        public void UpdateMaximizeState()
        {
            if (App.MainWindow?.AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                IsMaximized = overlapped.State == OverlappedPresenterState.Maximized;
            }
        }

        public void SyncFullScreenStateFromWindow()
        {
            IsFullScreen = App.MainWindow?.AppWindow.Presenter.Kind
                           == AppWindowPresenterKind.FullScreen;
        }
        public string InfoBarTitle { get; set => SetProperty(ref field, value); } = string.Empty;
        public bool InfoBarIsOpen { get; set => SetProperty(ref field, value); } = false;
        public string InfoBarMessage { get; set => SetProperty(ref field, value); } = string.Empty;
        public string PageType { get; set; } = string.Empty;
        public float ControlsStackOpacity { get; set => SetProperty(ref field, value); } = 0.0f;
        public bool IsBackBtnEnable { get; set => SetProperty(ref field, value); } = false;
        public TimeSpan LyricsDurationTime { get; set; } = TimeSpan.Zero;
        public bool IsManualSelect { get; set; } = false;
        public bool IsMouseOverVolumeSlider { get; set; } = false;
        private TimeSpan TotalTime { get; set; }
        private TimeSpan CurrentTime { get; set; }
        public TimeSpan CurrentPlayingTime { get; set => SetProperty(ref field, value); } = TimeSpan.Zero;
        private TimeProgressCache _cache;
        private DispatcherQueueTimer? _progressTimer;
        private CancellationTokenSource? _progressPollingCts;
        private Task? _progressPollingTask;
        private volatile bool _isDisposed;
        private DispatcherQueueHandler? _startTimerHandler;
        private DispatcherQueueHandler? _stopTimerHandler;
        private DispatcherQueueHandler? _updateProgressTimerHandler;
        private long _lastSmtcUpdateTick;
        public event Action<long>? CurrentPlayingTimeChanged;
        private SystemMediaControlsService SystemMediaControlsService { get; set; }

        private struct TimeProgressCache
        {
            private long _currentMs;
            private long _totalMs;
            public void Store(long c, long t) { Volatile.Write(ref _totalMs, t); Volatile.Write(ref _currentMs, c); }
            public (long curMs, long totalMs) Load() => (Volatile.Read(ref _currentMs), Volatile.Read(ref _totalMs));
        }

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
        public bool IsFogEffectEnabled
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
        public bool IsSnowEffectEnabled
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
        public bool IsRaindropEffectEnabled
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
                    IsPlayingBus.Publish(value);
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
            _progressPollingCts = new CancellationTokenSource();
            LyricsSyncRequestBus.Requested += SendFullLyricsSync;
        }

        public void UpdatePlayPauseButtonIcon()
        {
            App.MainWindow.UpdateTaskbarIcon();
            SystemMediaControlsService.UpdateSystemMediaControlsState();
        }
        private void HandleProgressSliderChange(double value)
        {
            if (IsMouseOverProgressBar && !IsUserDraggingProgressSlider)
            {
                var (curMs, totalMs) = GetTimeProgressCache();
                long newPosMs = (long)(value * 1000);
                if (Math.Abs(newPosMs - curMs) > 2000)
                {
                    _ = Task.Run(() =>
                    {
                        IsManualSelect = true;
                        App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(newPosMs);
                        SetTimeProgressCache(newPosMs, totalMs);
                        IsManualSelect = false;
                    });
                }
            }
        }
        private void EnqueueUnlessUIThread(ref DispatcherQueueHandler? cache, DispatcherQueueHandler work)
        {
            var window = App.MainWindow;
            if (window is null) return;
            var dq = window.DispatcherQueue;
            if (dq.HasThreadAccess) work();
            else dq.TryEnqueue(cache ??= work);
        }

        public void StartProgressTimer()
        {
            EnqueueUnlessUIThread(ref _startTimerHandler, StartProgressTimerCore);
        }

        private void StartProgressTimerCore()
        {
            if (_progressTimer is null)
            {
                _progressTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                _progressTimer.Interval = TimeSpan.FromMilliseconds(125);
                _progressTimer.IsRepeating = true;
                _progressTimer.Tick += OnProgressTick;
            }
            if (_progressPollingCts is null || _progressPollingCts.IsCancellationRequested)
            {
                _progressPollingCts?.Dispose();
                _progressPollingCts = new CancellationTokenSource();
            }
            if (_progressPollingTask is null || _progressPollingTask.IsCompleted)
            {
                _progressPollingTask = Task.Run(() => PollProgressLoopAsync(_progressPollingCts!.Token));
            }
            _progressTimer.Start();
        }

        public void StopProgressTimer()
        {
            EnqueueUnlessUIThread(ref _stopTimerHandler, StopProgressTimerCore);
        }

        private void StopProgressTimerCore()
        {
            _progressTimer?.Stop();
            _progressPollingCts?.Cancel();
        }

        public void UpdateProgressTimerUI()
        {
            EnqueueUnlessUIThread(ref _updateProgressTimerHandler, () => OnProgressTick(null, null!));
        }

        public (long curMs, long totalMs) GetTimeProgressCache() => _cache.Load();

        public void SetTimeProgressCache(long curMs, long totalMs) => _cache.Store(curMs, totalMs);

        public void SetTimeProgressCacheCurMs(long curMs)
        {
            var (_, oldTot) = _cache.Load();
            _cache.Store(curMs, oldTot);
        }

        private async Task PollProgressLoopAsync(CancellationToken ct)
        {
            var svc = App.Services.GetRequiredService<BassPlayerCommandService>();
            try
            {
                using var pt = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await pt.WaitForNextTickAsync(ct))
                {
                    try
                    {
                        var result = await svc.GetTimeProgress();
                        // null = round-trip failed (server busy/timeout); keep the last
                        // known value instead of storing (0, 0) and jumping the UI.
                        if (result is { } p) _cache.Store(p.currentMs, p.totalMs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "进度轮询失败");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private void OnProgressTick(DispatcherQueueTimer? sender, object args)
        {
            if (_isDisposed || IsUserDraggingProgressSlider) return;
            try
            {
                var (curMs, totalMs) = _cache.Load();
                TotalTime = TimeSpan.FromMilliseconds(totalMs);
                CurrentTime = TimeSpan.FromMilliseconds(curMs);
                CurrentPlayingTime = CurrentTime;
                CurrentPlayingTimeChanged?.Invoke(curMs);
                TimeProgressBus.Publish(curMs);

                if (IsManualSelect) return;

                ProgressSlider = curMs / 1000.0;
                ProgressSliderMax = totalMs / 1000.0;

                int currentSecond = (int)CurrentTime.TotalSeconds;
                if (currentSecond == _lastDisplayedSecond) return;
                _lastDisplayedSecond = currentSecond;

                PlayTimeText = CurrentTime.Hours >= 1
                    ? string.Create(17, (curMs, totalMs), WriteTimeWithHours)
                    : string.Create(11, (curMs, totalMs), WriteTimeNoHours);

                if (Environment.TickCount64 - _lastSmtcUpdateTick >= 250)
                {
                    _lastSmtcUpdateTick = Environment.TickCount64;
                    SystemMediaControlsService.UpdateTimelineProperties(CurrentTime, TotalTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateProgressTimerUI 更新进度条UI失败");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteTimeWithHours(Span<char> d, (long curMs, long totMs) s)
        {
            var cur = TimeSpan.FromMilliseconds(s.curMs);
            var tot = TimeSpan.FromMilliseconds(s.totMs);
            cur.Hours.TryFormat(d.Slice(0, 2),   out _, "D2", CultureInfo.InvariantCulture);
            d[2] = ':';
            cur.Minutes.TryFormat(d.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[5] = ':';
            cur.Seconds.TryFormat(d.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[8] = '/';
            tot.Hours.TryFormat(d.Slice(9, 2),   out _, "D2", CultureInfo.InvariantCulture);
            d[11] = ':';
            tot.Minutes.TryFormat(d.Slice(12, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[14] = ':';
            tot.Seconds.TryFormat(d.Slice(15, 2), out _, "D2", CultureInfo.InvariantCulture);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteTimeNoHours(Span<char> d, (long curMs, long totMs) s)
        {
            var cur = TimeSpan.FromMilliseconds(s.curMs);
            var tot = TimeSpan.FromMilliseconds(s.totMs);
            cur.Minutes.TryFormat(d.Slice(0, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[2] = ':';
            cur.Seconds.TryFormat(d.Slice(3, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[5] = '/';
            tot.Minutes.TryFormat(d.Slice(6, 2), out _, "D2", CultureInfo.InvariantCulture);
            d[8] = ':';
            tot.Seconds.TryFormat(d.Slice(9, 2), out _, "D2", CultureInfo.InvariantCulture);
        }

        public void LoadLyricsToUI(Music music)
        {
            _loadingMusicId = music.Id;
            _ = Task.Run(() => LoadLyricsCore(music));
        }

        private static async Task LoadLyricsCore(Music music)
        {
            var vm = App.Services.GetRequiredService<AppViewModel>();
            vm.LastLyricIndex = -1;
            var parsedLyrics = await App.Services.GetRequiredService<LyricsRefreshService>().SetLyrics(music);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (vm._loadingMusicId == music.Id)
                    vm.UILyrics = parsedLyrics;
            });
        }

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }

        public Music? FindById(int id)
        {
            if (_indexDirty) RebuildIdIndex();
            return _idIndex.TryGetValue(id, out var m) ? m : null;
        }

        public bool TryFindById(int id, out Music? music)
        {
            if (_indexDirty) RebuildIdIndex();
            return _idIndex.TryGetValue(id, out music);
        }

        public Music? FindFirstByAlbum(string? album)
        {
            if (string.IsNullOrEmpty(album)) return null;
            if (_indexDirty) RebuildIdIndex();
            return _firstAlbumIndex.TryGetValue(album, out var m) ? m : null;
        }

        public Music? FindFirstByArtist(string? artist)
        {
            if (string.IsNullOrEmpty(artist)) return null;
            if (_indexDirty) RebuildIdIndex();
            if (_firstArtistIndex.TryGetValue(artist, out var m)) return m;
            var names = ArtistHelper.GetArtistNames(artist);
            for (int i = 0; i < names.Length; i++)
            {
                if (_firstArtistIndex.TryGetValue(names[i], out m)) return m;
            }
            return null;
        }

        public Music? FindFirstByFolder(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            if (_indexDirty) RebuildIdIndex();
            return _firstFolderIndex.TryGetValue(folder, out var m) ? m : null;
        }

        public void NotifyIdIndexChanged() => _indexDirty = true;

        private void RebuildIdIndex()
        {
            _idIndex.Clear();
            _firstAlbumIndex.Clear();
            _firstArtistIndex.Clear();
            _firstFolderIndex.Clear();
            _albumSongCounts.Clear();
            var src = _songsSource;
            for (int i = 0; i < src.Count; i++)
            {
                var m = src[i];
                if (m is null) continue;
                _idIndex[m.Id] = m;
                if (!string.IsNullOrEmpty(m.Album))
                {
                    if (!_firstAlbumIndex.ContainsKey(m.Album))
                        _firstAlbumIndex[m.Album] = m;
                    _albumSongCounts[m.Album] = _albumSongCounts.GetValueOrDefault(m.Album) + 1;
                }
                if (!string.IsNullOrEmpty(m.Author))
                    AddArtistIndexEntry(m);
                if (!string.IsNullOrEmpty(m.LastLevelFolderPath) && !_firstFolderIndex.ContainsKey(m.LastLevelFolderPath))
                    _firstFolderIndex[m.LastLevelFolderPath] = m;
            }
            _indexDirty = false;
        }

        private void AddArtistIndexEntry(Music m)
        {
            var names = ArtistHelper.GetArtistNames(m.Author);
            for (int i = 0; i < names.Length; i++)
            {
                if (!_firstArtistIndex.ContainsKey(names[i]))
                    _firstArtistIndex[names[i]] = ArtistHelper.CreateArtistTile(m, names[i]);
            }
        }

        public int GetAlbumSongCount(string? album)
        {
            if (string.IsNullOrEmpty(album)) return 0;
            if (_indexDirty) RebuildIdIndex();
            return _albumSongCounts.TryGetValue(album, out var c) ? c : 0;
        }

        public void NotifySongsSourceChanged()
        {
            _indexDirty = true;
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
            MusicGroupDetailViewModel.UpdateAll(x => x.UpdateAlbumMenuOptionsPlayList());
            App.Services.GetRequiredService<PlaylistDetailViewModel>().UpdateAlbumMenuOptionsPlayList();
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
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
                    SongViewType.Artist => m =>
                        (m.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (m.Author?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false),
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

            var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
            var pool = ArrayPool<Music>.Shared;
            var buf = pool.Rent(Math.Max(srcSpan.Length, 1));
            int count = 0;
            try
            {
                if (combinedPredicate != null)
                {
                    for (int i = 0; i < srcSpan.Length; i++)
                    {
                        if (combinedPredicate(srcSpan[i]))
                            buf[count++] = srcSpan[i];
                    }
                }
                else
                {
                    srcSpan.CopyTo(buf);
                    count = srcSpan.Length;
                }

                var slice = buf.AsSpan(0, count);
                var tag = SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder";
                IComparer<Music> comparer;
                if (tag == "DefaultOrder")
                {
                    comparer = viewType switch
                    {
                        SongViewType.Album => _songByDiskTrack,
                        SongViewType.Artist or SongViewType.Folder => _songByAlbumDiskTrack,
                        SongViewType.Favorite => _songByOrderDesc,
                        _ => _songByTitle
                    };
                }
                else
                {
                    comparer = tag switch
                    {
                        "A-Z" => _songByTitle,
                        "Artist" => _songByAuthor,
                        "Album" => _songByAlbumTrack,
                        "CreateTimeASC" => _songByCreateTimeAsc,
                        "CreateTimeDESC" => _songByCreateTimeDesc,
                        "UpdateTimeASC" => _songByUpdateTimeAsc,
                        "UpdateTimeDESC" => _songByUpdateTimeDesc,
                        _ => _songByTitle
                    };
                }
                slice.Sort(comparer);

                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    targetCollection.FillFrom(buf.AsSpan(0, count));
                });
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }

        public void UpdateGroupedByFirstLetter(Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
        {
            try
            {
                var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
                bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
                var search = SearchText;

                var distinctMap = new Dictionary<string, Music>(srcSpan.Length / 4 + 1);
                for (int i = 0; i < srcSpan.Length; i++)
                {
                    ref readonly var m = ref srcSpan[i];
                    if (hasSearch && !MatchesSearchShape(m, search))
                        continue;

                    var key = distinctSelector(m);
                    distinctMap.TryAdd(key, m);
                }

                PublishGroupedSource(distinctMap, distinctSelector, groupSelector, source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public void UpdateArtistGroupedByFirstLetter(CollectionViewSource source)
        {
            try
            {
                var srcSpan = CollectionsMarshal.AsSpan(SongsSource);
                bool hasSearch = !string.IsNullOrWhiteSpace(SearchText);
                var search = SearchText;

                var distinctMap = new Dictionary<string, Music>(srcSpan.Length / 4 + 1);
                for (int i = 0; i < srcSpan.Length; i++)
                {
                    ref readonly var m = ref srcSpan[i];
                    if (hasSearch && !MatchesSearchShape(m, search))
                        continue;

                    var names = ArtistHelper.GetArtistNames(m.Author);
                    for (int n = 0; n < names.Length; n++)
                    {
                        if (!distinctMap.ContainsKey(names[n]))
                            distinctMap[names[n]] = ArtistHelper.CreateArtistTile(m, names[n]);
                    }
                }

                PublishGroupedSource(distinctMap, m => m.Author, m => GetFirstLetterAdvanced(m.Author), source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        private static bool MatchesSearchShape(Music m, string search)
        {
            return (m.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (m.Album?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (m.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (m.LastLevelFolderPath?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private void PublishGroupedSource(Dictionary<string, Music> distinctMap, Func<Music, string> distinctSelector, Func<Music, string> groupSelector, CollectionViewSource source)
        {
            var pool = ArrayPool<Music>.Shared;
            var buf = pool.Rent(Math.Max(distinctMap.Count, 1));
            int count = 0;
            try
            {
                foreach (var kvp in distinctMap)
                    buf[count++] = kvp.Value;

                var slice = buf.AsSpan(0, count);
                IComparer<Music> comparer = (SelectedSortOption.Tag as string) switch
                {
                    "A-Z" => _songByTitle,
                    "Artist" => _songByAuthor,
                    "Album" => Comparer<Music>.Create((a, b) => string.Compare(a.Album, b.Album, StringComparison.Ordinal)),
                    "CreateTimeASC" => _songByCreateTimeAsc,
                    "CreateTimeDESC" => _songByCreateTimeDesc,
                    "UpdateTimeASC" => _songByUpdateTimeAsc,
                    "UpdateTimeDESC" => _songByUpdateTimeDesc,
                    _ => Comparer<Music>.Create((a, b) => string.Compare(distinctSelector(a), distinctSelector(b), StringComparison.Ordinal)),
                };
                slice.Sort(comparer);

                var groupDict = new Dictionary<string, List<Music>>(count / 4 + 1);
                for (int i = 0; i < count; i++)
                {
                    var gKey = groupSelector(buf[i]);
                    if (!groupDict.TryGetValue(gKey, out var list))
                    {
                        list = new List<Music>();
                        groupDict[gKey] = list;
                    }
                    list.Add(buf[i]);
                }

                var groups = new List<MusicGroup>(groupDict.Count);
                foreach (var kvp in groupDict)
                    groups.Add(new MusicGroup(kvp.Key, kvp.Value));
                groups.Sort((a, b) => string.Compare(
                    a.Key == "ZZZ" ? "#" : a.Key,
                    b.Key == "ZZZ" ? "#" : b.Key,
                    StringComparison.Ordinal));

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    source.Source = groups;
                });
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }

        public void AddMusicToCurrentPlayList(Music music)
        {
            if (music is null) return;
            int index = GetCurrentIndex();
            if (index == -1)
                CurrentPlayingList.Add(music);
            else
                CurrentPlayingList.Insert(index + 1, music);
        }

        public void AddMusicRangeToCurrentPlayList(IEnumerable<Music> musics)
        {
            if (musics is null) return;

            var batch = new List<Music>();
            foreach (var m in musics)
            {
                if (m is not null) batch.Add(m);
            }
            if (batch.Count == 0) return;

            int index = GetCurrentIndex();
            if (index == -1)
            {
                CurrentPlayingList.AddRange(batch);
                return;
            }

            CurrentPlayingList.InsertRange(index + 1, batch);        
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

        private void OnSearchDebounceElapsed(DispatcherQueueTimer sender, object args)
        {
            RefreshDataSource();
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
            _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, CurrentArtistObj?.Author ?? ""));
            _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
            _ = RefreshPlayListSongMapping();
            UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            UpdateArtistGroupedByFirstLetter(ArtistPageSource);
            UpdateGroupedByFirstLetter(m => m.LastLevelFolderPath, m => GetFirstLetterAdvanced(m.LastLevelFolderPath), FolderPageSource);
            App.Services.GetRequiredService<MusicBrowseViewModel>()?.UpdateViewList();
        }

        private void RefreshDataForPageType(Type pageType)
        {
            if (pageType == typeof(SongListPage))
            {
                _ = UpdateSongCollectionsAsync(ListSongs, SongViewType.All);
            }
            else if (pageType == typeof(AlbumPage))
            {
                _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
                UpdateGroupedByFirstLetter(m => m.Album, m => GetFirstLetterAdvanced(m.Album), AlbumPageSource);
            }
            else if (pageType == typeof(ArtistPage))
            {
                _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, CurrentArtistObj?.Author ?? ""));
                UpdateArtistGroupedByFirstLetter(ArtistPageSource);
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
            else if (pageType == typeof(PlayListPage))
            {
                _ = RefreshPlayListSongMapping();
            }
            else
            {
                return;
            }
            App.Services.GetRequiredService<MusicBrowseViewModel>()?.UpdateViewList();
        }

        public async Task RefreshPlayListSongMapping()
        {
            var plmSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(AppData.AllPlayListMusics);
            int plmCount = plmSpan.Length;
            int curListId = CurrentPlayListId;
            var search = SearchText;
            bool hasSearch = !string.IsNullOrWhiteSpace(search);

            int upper = plmCount;
            var pool = System.Buffers.ArrayPool<PlayListMusicItem>.Shared;
            var buf = pool.Rent(upper);
            int written = 0;
            try
            {
                for (int i = 0; i < plmCount; i++)
                {
                    ref readonly var plm = ref plmSpan[i];
                    if (plm.PlayListId != curListId) continue;
                    if (!TryFindById(plm.MusicId, out var m) || m is null) continue;
                    if (hasSearch && !MusicMatchesSearch(m, search)) continue;
                    buf[written++] = new PlayListMusicItem { Music = m, PlayListOrder = plm.Order };
                }

                var slice = buf.AsSpan(0, written);
                slice.Sort(_byPlayListOrderDesc);

                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    PlayListSongs.FillFrom(buf.AsSpan(0, written));
                });
            }
            finally
            {
                pool.Return(buf, clearArray: false);
            }
        }


        private static bool MusicMatchesSearch(Music m, string search)
        {
            return (m.Title is not null && m.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                   (m.Album is not null && m.Album.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                   (m.Author is not null && m.Author.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byPlayListOrderDesc =
            Comparer<PlayListMusicItem>.Create((a, b) => b.PlayListOrder.CompareTo(a.PlayListOrder));
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicTitleAsc =
            Comparer<PlayListMusicItem>.Create((a, b) => string.Compare(a.Music?.Title, b.Music?.Title, StringComparison.Ordinal));
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicAuthorAsc =
            Comparer<PlayListMusicItem>.Create((a, b) => string.Compare(a.Music?.Author, b.Music?.Author, StringComparison.Ordinal));
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicAlbumAsc =
            Comparer<PlayListMusicItem>.Create((a, b) => string.Compare(a.Music?.Album, b.Music?.Album, StringComparison.Ordinal));
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicCreateTimeAsc =
            Comparer<PlayListMusicItem>.Create((a, b) => a.Music?.CreateTime.CompareTo(b.Music?.CreateTime) ?? 0);
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicCreateTimeDesc =
            Comparer<PlayListMusicItem>.Create((a, b) => b.Music?.CreateTime.CompareTo(a.Music?.CreateTime) ?? 0);
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicUpdateTimeDesc =
            Comparer<PlayListMusicItem>.Create((a, b) => b.Music?.UpdateTime.CompareTo(a.Music?.UpdateTime) ?? 0);
        private static readonly System.Collections.Generic.IComparer<PlayListMusicItem> _byMusicUpdateTimeAsc =
            Comparer<PlayListMusicItem>.Create((a, b) => a.Music?.UpdateTime.CompareTo(b.Music?.UpdateTime) ?? 0);

        private static readonly IComparer<Music> _songByTitle =
            Comparer<Music>.Create((a, b) => string.Compare(a.Title, b.Title, StringComparison.Ordinal));
        private static readonly IComparer<Music> _songByAuthor =
            Comparer<Music>.Create((a, b) => string.Compare(a.Author, b.Author, StringComparison.Ordinal));
        private static readonly IComparer<Music> _songByAlbumTrack =
            Comparer<Music>.Create((a, b) =>
            {
                int c = string.Compare(a.Album, b.Album, StringComparison.Ordinal);
                return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
            });
        private static readonly IComparer<Music> _songByDiskTrack =
            Comparer<Music>.Create((a, b) =>
            {
                int c = a.DiskNumber.CompareTo(b.DiskNumber);
                return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
            });
        private static readonly IComparer<Music> _songByAlbumDiskTrack =
            Comparer<Music>.Create((a, b) =>
            {
                int c = string.Compare(a.Album, b.Album, StringComparison.Ordinal);
                if (c != 0) return c;
                c = a.DiskNumber.CompareTo(b.DiskNumber);
                return c != 0 ? c : a.TrackNumber.CompareTo(b.TrackNumber);
            });
        private static readonly IComparer<Music> _songByOrderDesc =
            Comparer<Music>.Create((a, b) => b.Order.CompareTo(a.Order));
        private static readonly IComparer<Music> _songByCreateTimeAsc =
            Comparer<Music>.Create((a, b) => a.CreateTime.CompareTo(b.CreateTime));
        private static readonly IComparer<Music> _songByCreateTimeDesc =
            Comparer<Music>.Create((a, b) => b.CreateTime.CompareTo(a.CreateTime));
        private static readonly IComparer<Music> _songByUpdateTimeAsc =
            Comparer<Music>.Create((a, b) => a.UpdateTime.CompareTo(b.UpdateTime));
        private static readonly IComparer<Music> _songByUpdateTimeDesc =
            Comparer<Music>.Create((a, b) => b.UpdateTime.CompareTo(a.UpdateTime));

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

        public async Task RefreshSongsSourceAsync()
        {
            var musicList = await _musicDatabaseService.GetMusicListAsync().ConfigureAwait(false);

            var dq = App.MainWindow.DispatcherQueue;
            if (dq.HasThreadAccess)
            {
                SongsSource.Clear();
                SongsSource.AddRange(musicList);
                _indexDirty = true;
                NotifySongsSourceChanged();
            }
            else
            {
                await dq.EnqueueAsync(() =>
                {
                    SongsSource.Clear();
                    SongsSource.AddRange(musicList);
                    _indexDirty = true;
                    NotifySongsSourceChanged();
                });
            }
        }

        public void RemoveFromSongsSource(Music music)
        {
            SongsSource.Remove(music);
            _idIndex.Remove(music.Id);
            _indexDirty = true;
            NotifySongsSourceChanged();
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
            MusicGroupDetailViewModel.UpdateAll(x => x.UpDateUsbDeviceMenuflyout());
            App.Services.GetRequiredService<SongListViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<PlaylistDetailViewModel>().UpDateUsbDeviceMenuflyout();
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
            if (uniqueSelectedMusics is not null && uniqueSelectedMusics.AsValueEnumerable().Any())
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

        public async Task EditPlayListName(PlayList playList, Func<Task<string>> getNameCallback)
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

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                _isDisposed = true;
                EnqueueUnlessUIThread(ref _stopTimerHandler, StopProgressTimerCore);
                _progressPollingCts?.Cancel();
                _progressPollingCts?.Dispose();
                _searchDebounceTimer?.Stop();
            }
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
            AnimatedWin2dControls.Messages.LyricsSettingsBus.Publish(new AnimatedWin2dControls.Messages.LyricsSettingsBus.Settings(
                fontFamilyName: fontFamilyName,
                lyricsTextAlignment: LyricsAlignment,
                isDark: IsDarkMode,
                scrollSensitivity: 1.0,
                lyricsBlurAmount: LyricsBlurAmount,
                glowAmount: GlowAmount,
                charFloatAmount: CharFloatAmount,
                charScaleAmount: CharScaleAmount,
                longSyllableThreshold: LongSyllableThreshold,
                isFadeOutEnabled: true,
                isOutOfSightEnabled: true,
                unplayedOpacity: UnplayedOpacityPercent / 100.0,
                translatedOpacity: TranslatedOpacityPercent / 100.0,
                strokeWidth: 0.0,
                scrollEasingType: ScrollEasingType,
                scrollEasingMode: ScrollEasingMode,
                playingLineTopOffset: PlayingLineTopOffsetPercent / 100.0,
                targetFrameRate: TargetFrameRate,
                isCustomColorEnabled: IsCustomLyricsColorEnabled,
                lyricsCustomColor: LyricsCustomColor,
                fontWeight: LyricsFontWeight));
        }

        private void SendLyricsFontSize()
        {
            double fontSize = IsGlobalFontSizeEnabled ? GlobalFontSize : LyricsFontSize;
            AnimatedWin2dControls.Messages.LyricsFontSizeBus.Publish(fontSize);
        }

        private void SendFullLyricsSync()
        {
            _settingsDebounceTimer?.Stop();
            SendLyricsSettings();
            SendLyricsFontSize();
            AnimatedWin2dControls.Messages.IsPlayingBus.Publish(IsPlaying);
            AnimatedWin2dControls.Messages.TimeProgressBus.Publish((long)CurrentTime.TotalMilliseconds);
            AnimatedWin2dControls.Messages.OffsetMsBus.Publish(CurrentPlayingMusic?.LyricsOffsetMs ?? 0);
            if (UILyrics.Count > 0)
                AnimatedWin2dControls.Messages.UILyricsBus.Publish(UILyrics);
        }

        [RelayCommand]
        private void OnVolumeSliderIconButtonChanged()
        {
            IsMuted = !IsMuted;
            Volume = IsMuted ? 0 : TempVolume;
        }

        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            ToggleFullScreen();
        }

        [RelayCommand]
        private void OnStopButtonChanged()
        {
            App.Services.GetRequiredService<BassPlayerCommandService>().MusicEnd();
            ProgressSlider = 0;
        }

        [RelayCommand]
        private void OnFastForwardButton()
        {
            SeekRelative(30_000);
        }
        [RelayCommand]
        private void OnFastBackwardButton()
        {
            SeekRelative(-10_000);
        }

        [RelayCommand]
        private void VolumeUp() => AdjustVolume(10);

        [RelayCommand]
        private void VolumeDown() => AdjustVolume(-10);

        private void SeekRelative(long deltaMs)
        {
            var (curMs, totalMs) = GetTimeProgressCache();
            long newPosMs = Math.Clamp(curMs + deltaMs, 0, totalMs);
            IsManualSelect = true;
            SetTimeProgressCache(newPosMs, totalMs);
            ProgressSlider = newPosMs / 1000.0;
            App.Services.GetRequiredService<BassPlayerCommandService>().ChangeWaveChannelTime(newPosMs);
            IsManualSelect = false;
        }
    }
}