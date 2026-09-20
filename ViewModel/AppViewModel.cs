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
        // 歌词迟到守卫票据：LoadLyricsToUI 可能在后台线程（IPC 自动切歌）调用，递增须原子。
        private int _lyricsLoadTicket;
        public Music? CurrentArtistObj { get => State.Browse.CurrentArtistObj; set => State.Browse.CurrentArtistObj = value; }
        public Music? CurrentAlbumObj { get => State.Browse.CurrentAlbumObj; set => State.Browse.CurrentAlbumObj = value; }
        public Music? CurrentFolderObj { get => State.Browse.CurrentFolderObj; set => State.Browse.CurrentFolderObj = value; }
        public PlayMode CurrentPlayMode
        {
            get => State.Queue.Mode;
            set => State.Queue.Mode = value;
        }

        /// <summary>按枚举名设置播放模式（托盘菜单 CommandParameter 传入），标题文本随 setter 统一同步。</summary>
        [RelayCommand]
        private void SetPlayMode(string mode)
        {
            if (Enum.TryParse(mode, true, out PlayMode playMode))
                CurrentPlayMode = playMode;
        }

        public string SearchText { get => State.Browse.SearchText; set => State.Browse.SearchText = value; }
        private DispatcherQueueTimer? _searchDebounceTimer;

        public List<Music> SongsSource
        {
            get => State.Library.Songs;
            set
            {
                if (ReferenceEquals(State.Library.Songs, value)) return;
                State.Library.Replace(value);
            }
        }
        public BulkObservableCollection<Music> FavoriteSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<PlayList> AllPlayList { get; set => SetProperty(ref field, value); } = [];
        public PlayList CurrentPlayList { get => State.Browse.CurrentPlayList; set => State.Browse.CurrentPlayList = value; }
        public int CurrentPlayListId { get => State.Browse.CurrentPlayListId; set => State.Browse.CurrentPlayListId = value; }
        public CollectionViewSource AlbumPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource ArtistPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public CollectionViewSource FolderPageSource { get; set => SetProperty(ref field, value); } = new CollectionViewSource() { IsSourceGrouped = true };
        public BulkObservableCollection<Music> ListSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> AlbumSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> ArtistSongs { get; set => SetProperty(ref field, value); } = [];
        public BulkObservableCollection<Music> FolderSongs { get; set => SetProperty(ref field, value); } = [];
        public Music? CurrentPlayingMusic { get => State.Playback.CurrentPlayingMusic; set => State.Playback.CurrentPlayingMusic = value; }
        public BulkObservableCollection<Music> SequentialPlayingList
        {
            get => State.Queue.Sequential;
            set { if (CanPublishState) State.Queue.Replace(value); }
        }
        public BulkObservableCollection<Music> CurrentPlayingList => State.Queue.Playing;
        public SortOption SelectedSortOption { get => State.Browse.SelectedSortOption; set => State.Browse.SelectedSortOption = value; }

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
        public string PlayTimeText { get => State.Playback.PlayTimeText; set => State.Playback.PlayTimeText = value; }
        //public string ProgressSliderThumbTipText { get; set => SetProperty(ref field, value); } = "00:00";
        public double ProgressSliderMax { get => State.Playback.ProgressSliderMax; set => State.Playback.ProgressSliderMax = value; }
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
        // 兼容现有设置持久化守卫；不再拥有可写的第二份就绪状态。
        public bool IsInitialized => _lifecycle.IsReady;
        private readonly AppLifecycle _lifecycle;
        public Visibility UsbDeviceVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        // 播放引擎就绪 = 生命周期 Ready 且 IPC 已连接且首曲/设置推送完成；由 StartupCoordinator 在
        // 两处条件变化点统一刷新，播放入口（命令 CanExecute 与按钮 IsEnabled）据此置灰。
        public bool IsPlaybackEngineReady { get => State.Playback.IsPlaybackEngineReady; set => State.Playback.IsPlaybackEngineReady = value; }
        // 全局进行中任务（IPC 连接/库加载/库扫描/文件监视重扫/USB 传输）的唯一状态源，
        // 驱动 MainPage 全页进度层；多操作并发时逐行显示文本，仅单操作上报百分比时进度环用确定进度。
        public ProgressCenter Progress => State.Operations;
        // 空音乐库占位（MusicBrowsePage 内容区）。初始 Collapsed，待首次 NotifySongsSourceChanged（DB 加载完成）后才置 Visible，避免启动加载期闪现。
        public Visibility LibraryEmptyVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        // 最爱页占位：库非空但没有任何收藏时显示（FavouritePlayListPage）；搜索过滤导致的空列表不显示。
        public Visibility FavoriteEmptyVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
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
        public bool IsPlayingDetailVisible
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                    App.Services.GetRequiredService<DesktopLyrics.DesktopLyricsViewModel>().IsPlayingDetailVisible = value;
            }
        } = false;
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
        public bool IsManualSelect { get => State.Playback.IsManualSelect; set => State.Playback.IsManualSelect = value; }
        public bool IsMouseOverVolumeSlider { get; set; } = false;
        public TimeSpan CurrentPlayingTime { get => State.Playback.CurrentPlayingTime; set => State.Playback.CurrentPlayingTime = value; }
        private volatile bool _isDisposed;
        private readonly PlaybackProgressService _playbackProgress;
        public event Action<long>? CurrentPlayingTimeChanged
        {
            add => _playbackProgress.CurrentPlayingTimeChanged += value;
            remove => _playbackProgress.CurrentPlayingTimeChanged -= value;
        }
        private SystemMediaControlsService SystemMediaControlsService { get; set; }


        // 带有复杂逻辑的属性重构
        public bool UseImageDominantTheme { get => State.Preferences.UseImageDominantTheme; set => State.Preferences.UseImageDominantTheme = value; }

        public bool IsFluidBackgroundEnabled { get => State.Preferences.IsFluidBackgroundEnabled; set => State.Preferences.IsFluidBackgroundEnabled = value; }
        public bool IsFogEffectEnabled { get => State.Preferences.IsFogEffectEnabled; set => State.Preferences.IsFogEffectEnabled = value; }
        public bool IsSnowEffectEnabled { get => State.Preferences.IsSnowEffectEnabled; set => State.Preferences.IsSnowEffectEnabled = value; }
        public bool IsRaindropEffectEnabled { get => State.Preferences.IsRaindropEffectEnabled; set => State.Preferences.IsRaindropEffectEnabled = value; }
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

        public Thickness LyricsMargin { get => State.Preferences.LyricsMargin; set => State.Preferences.LyricsMargin = value; }

        public bool IsUserDraggingProgressSlider { get => State.Playback.IsUserDraggingProgressSlider; set => State.Playback.IsUserDraggingProgressSlider = value; }

        public double ProgressSlider { get => State.Playback.ProgressSlider; set => State.Playback.ProgressSlider = value; }

        public bool IsPlaying { get => State.Playback.IsPlaying; set => State.Playback.IsPlaying = value; }

        public bool IsMouseOverProgressBar { get => State.Playback.IsMouseOverProgressBar; set => State.Playback.IsMouseOverProgressBar = value; }

        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<AppViewModel> _logger;

        public WinUIMusicPlayer.State.AppState State { get; }
        private readonly LibraryQueries _libraryQueries;
        private readonly LibraryProjectionService _projections;

        public AppViewModel(MusicDatabaseService musicDatabaseService, SystemMediaControlsService systemMediaControlsService, ILogger<AppViewModel> logger, UsbDeviceService usbDeviceService, LicenseService licenseService, AppLifecycle lifecycle, WinUIMusicPlayer.State.AppState state, LibraryQueries libraryQueries, PlaybackProgressService playbackProgress, LibraryProjectionService projections)
        {
            State = state;
            _libraryQueries = libraryQueries;
            _playbackProgress = playbackProgress;
            _projections = projections;
            State.Playback.PropertyChanged += OnPlaybackStateChanged;
            State.Preferences.PropertyChanged += OnPreferencesStateChanged;
            State.Browse.PropertyChanged += OnBrowseStateChanged;
            State.Output.PropertyChanged += OnPreferencesStateChanged;
            State.DesktopLyrics.PropertyChanged += OnDesktopLyricsStateChanged;
            State.Queue.PropertyChanged += OnQueueStateChanged;
            _lifecycle = lifecycle;
            _musicDatabaseService = musicDatabaseService;
            SystemMediaControlsService = systemMediaControlsService;
            _logger = logger;
            UsbDeviceService = usbDeviceService;
            _licenseService = licenseService;
            PurchaseLicenseCommand = new AsyncRelayCommand(licenseService.PurchaseAsync, () => licenseService.CanPurchase);
            licenseService.StateChanged += OnLicenseStateChanged;
            ApplyLicenseState();
            usbDeviceService.DevicesChanged += OnUsbDevicesChanged;
            usbDeviceService.DeviceMusicChanged += OnUsbMusicChanged;
            AllPlayList.CollectionChanged += AllPlayList_CollectionChanged;
            FavoriteSongs.CollectionChanged += FavoriteSongs_CollectionChanged;
            LyricsSyncRequestBus.Requested += SendFullLyricsSync;
        }

        private void OnBrowseStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(SearchText):
                    if (_searchDebounceTimer is null)
                    {
                        _searchDebounceTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
                        _searchDebounceTimer.IsRepeating = false;
                        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;
                    }
                    _searchDebounceTimer.Stop();
                    _searchDebounceTimer.Start();
                    break;
                case nameof(SelectedSortOption):
                    OnSelectSortChanged();
                    break;
                case nameof(CurrentArtistObj):
                    _ = UpdateSongCollectionsAsync(ArtistSongs, SongViewType.Artist, m => ArtistHelper.IsMusicByArtist(m, CurrentArtistObj?.Author ?? ""));
                    break;
                case nameof(CurrentAlbumObj):
                    _ = UpdateSongCollectionsAsync(AlbumSongs, SongViewType.Album, m => m.Album == CurrentAlbumObj?.Album);
                    break;
                case nameof(CurrentFolderObj):
                    _ = UpdateSongCollectionsAsync(FolderSongs, SongViewType.Folder, m => m.LastLevelFolderPath == CurrentFolderObj?.LastLevelFolderPath);
                    break;
            }
        }

        private void OnPlaybackStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
            if (e.PropertyName == nameof(ProgressSlider)) HandleProgressSliderChange(ProgressSlider);
            else if (e.PropertyName == nameof(CurrentPlayingMusic) && IsInitialized)
                OffsetMsBus.Publish(CurrentPlayingMusic?.LyricsOffsetMs ?? 0);
            else if (e.PropertyName == nameof(IsPlaying))
            {
                AppData.IsPlaying = IsPlaying;
                UpdatePlayPauseButtonIcon();
                IsPlayingBus.Publish(IsPlaying);
            }
        }

        internal void NotifyPreferenceComputed(string name) => OnPropertyChanged(name);

        private void OnPreferencesStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            => OnPropertyChanged(e);

        private void OnQueueStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string? name = e.PropertyName switch
            {
                nameof(State.Queue.Mode) => nameof(CurrentPlayMode),
                nameof(State.Queue.Sequential) => nameof(SequentialPlayingList),
                nameof(State.Queue.Playing) => nameof(CurrentPlayingList),
                _ => null
            };
            if (name is not null) OnPropertyChanged(name);
        }

        private void OnLicenseStateChanged() => EnqueueUnlessUIThread(ref _licenseStateChangedHandler, ApplyLicenseState);
        private void OnUsbDevicesChanged(object? sender, EventArgs e) { if (!_isDisposed) UpDateUsbDeviceMenuflyout(); }
        private void OnUsbMusicChanged(object? sender, EventArgs e) { if (!_isDisposed) RefreshUsbDeviceMusicList(); }
        private void OnDesktopLyricsStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State.DesktopLyrics.IsKaraokeEnabled))
            {
                AppSettings.IsDesktopLyricsKaraokeEnabled = State.DesktopLyrics.IsKaraokeEnabled;
                OnPropertyChanged(nameof(IsDesktopLyricsKaraokeEnabled));
                if (IsInitialized) _ = _musicDatabaseService.SaveSettingAsync();
            }
        }

        /// <summary>所有播放入口及迟到回调共用的生命周期守卫。</summary>
        public bool CanStartPlayback => !_isDisposed && IsInitialized && IsPlaybackEngineReady;
        public bool CanPublishState => !_isDisposed && _lifecycle.Phase != AppPhase.Stopping;

        /// <summary>USB 设备生命周期（发现/选中/台账/扫描）唯一归属。</summary>
        public UsbDeviceService UsbDeviceService { get; }

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
                    IsManualSelect = true;
                    App.Services.GetRequiredService<PlaybackCommands>().SeekCommand.Execute(newPosMs);
                    IsManualSelect = false;
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

        public void StartProgressTimer() => _playbackProgress.StartProgressTimer();
        public void StopProgressTimer() => _playbackProgress.StopProgressTimer();
        public void UpdateProgressTimerUI() => _playbackProgress.UpdateProgressTimerUI();
        public (long curMs, long totalMs) GetTimeProgressCache() => _playbackProgress.GetTimeProgressCache();
        public void BeginProgressSeek(long positionMs, long seekId) => _playbackProgress.BeginProgressSeek(positionMs, seekId);
        public void CancelProgressSeek(long seekId) => _playbackProgress.CancelProgressSeek(seekId);
        public void MarkPlaybackEnded(long totalMs) => _playbackProgress.MarkPlaybackEnded(totalMs);

        public void LoadLyricsToUI(Music music)
        {
            if (!CanPublishState) return;
            LastLyricIndex = -1;
            // 按递增票据匹配而非 Music.Id：一次性外部曲目 Id 均为 0，按 Id 匹配会放过上一首的迟到结果。
            int ticket = Interlocked.Increment(ref _lyricsLoadTicket);
            _ = App.Services.GetRequiredService<ApplicationTasks>().RunAsync(_ => LoadLyricsCore(music, ticket));
        }

        private static async Task LoadLyricsCore(Music music, int ticket)
        {
            var vm = App.Services.GetRequiredService<AppViewModel>();
            try
            {
                var service = App.Services.GetRequiredService<LyricsRefreshService>();
                var parsedLyrics = await Task.Run(() => service.SetLyrics(music));
                if (vm.CanPublishState && Volatile.Read(ref vm._lyricsLoadTicket) == ticket)
                    vm.UILyrics = parsedLyrics;
            }
            catch (Exception ex) { vm._logger.LogError(ex, "加载歌词失败"); }
        }

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }

        public Music? FindById(int id) => _libraryQueries.FindById(id);
        public bool TryFindById(int id, out Music? music) => _libraryQueries.TryFindById(id, out music);
        public Music? FindFirstByAlbum(string? album) => _libraryQueries.FindFirstByAlbum(album);
        public Music? FindFirstByArtist(string? artist) => _libraryQueries.FindFirstByArtist(artist);
        public Music? FindFirstByFolder(string? folder) => _libraryQueries.FindFirstByFolder(folder);
        public int GetAlbumSongCount(string? album) => _libraryQueries.GetAlbumSongCount(album);
        public void NotifyIdIndexChanged() => _libraryQueries.Invalidate();

        public void NotifySongsSourceChanged()
        {
            _libraryQueries.Invalidate();
            LibraryEmptyVisibility = State.Library.Songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (IsInitialized)
            {
                RefreshAllViews();
            }
            else
            {
                // 主界面已先行显示（无 Loading 过渡层）：缓存库到达时刷新当前页，
                // 成本与首次导航构建相同；其余调用路径均在 Ready 之后走全量分支。
                RefreshDataSource();
            }
        }

        private void AllPlayList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateMenuOptionsPlayList();
        }

        public event Action? PlaylistMenusChanged;
        public event Action? UsbMenusChanged;

        public void UpdateMenuOptionsPlayList()
        {
            App.Services.GetRequiredService<AlbumViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<ArtistViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<FolderViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<SongListViewModel>().UpdateAlbumMenuOptionsPlayList();
            App.Services.GetRequiredService<FavouritePlayListViewModel>().UpdateAlbumMenuOptionsPlayList();
            PlaylistMenusChanged?.Invoke();
            App.Services.GetRequiredService<PlaylistDetailViewModel>().UpdateAlbumMenuOptionsPlayList();
        }

        public Task UpdateSongCollectionsAsync(BulkObservableCollection<Music> target, SongViewType kind, Func<Music, bool>? filter = null)
            => _projections.UpdateSongCollectionsAsync(target, kind, filter);
        public void UpdateGroupedByFirstLetter(Func<Music, string> distinct, Func<Music, string> group, CollectionViewSource source)
            => _projections.UpdateGroupedByFirstLetter(distinct, group, source);
        public void UpdateArtistGroupedByFirstLetter(CollectionViewSource source)
            => _projections.UpdateArtistGroupedByFirstLetter(source);

        public void AddMusicToCurrentPlayList(Music music)
        {
            if (music is not null) State.Queue.InsertNext(CurrentPlayingMusic, [music]);
        }

        public void AddMusicRangeToCurrentPlayList(IEnumerable<Music> musics)
        {
            if (musics is not null) State.Queue.InsertNext(CurrentPlayingMusic, musics);
        }

        public int GetCurrentIndex() => State.Queue.IndexOf(CurrentPlayingMusic);

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
            _libraryQueries.Invalidate();
            // 收藏/歌单映射用于跨页操作；其他展示投影在导航到对应页时按版本重建。
            _ = UpdateSongCollectionsAsync(FavoriteSongs, SongViewType.Favorite, m => m.IsFavorite == true);
            _ = RefreshPlayListSongMapping();
            RefreshDataSource();
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

        public Task RefreshPlayListSongMapping() => _projections.RefreshPlayListSongMapping(PlayListSongs);

        public void OnSelectSortChanged()
        {
            RefreshDataSource();
        }

        /// <summary>
        /// 当歌曲被设为收藏时调用
        /// </summary>
        public void AddToFavoriteSongs(Music music)
        {
            _libraryQueries.Invalidate();
            FavoriteSongs.Insert(0, music);
        }

        /// <summary>
        /// 当歌曲被取消收藏时调用
        /// </summary>
        public void RemoveFromFavoriteSongs(Music music)
        {
            _libraryQueries.Invalidate();
            FavoriteSongs.Remove(music);
        }

        // FavoriteSongs 的所有变更（FillFrom 整表刷新 / 单首增删）都会经过 CollectionChanged，
        // 这里是"最爱页是否为空"的唯一汇聚点；FillFrom 无条件触发 Reset，空->空 也会重新求值。
        private void FavoriteSongs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            FavoriteEmptyVisibility = FavoriteSongs.Count == 0 && string.IsNullOrWhiteSpace(SearchText)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public async Task RefreshSongsSourceAsync(CancellationToken cancellationToken = default)
        {
            var musicList = await _musicDatabaseService.GetMusicListAsync().ConfigureAwait(false);

            var dq = App.MainWindow.DispatcherQueue;
            if (dq.HasThreadAccess)
            {
                if (cancellationToken.IsCancellationRequested) return;
                SongsSource.Clear();
                SongsSource.AddRange(musicList);
                ReconcilePlaybackLibrary();
                _libraryQueries.Invalidate();
                NotifySongsSourceChanged();
            }
            else
            {
                await dq.EnqueueAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    SongsSource.Clear();
                    SongsSource.AddRange(musicList);
                    ReconcilePlaybackLibrary();
                    _libraryQueries.Invalidate();
                    NotifySongsSourceChanged();
                });
            }
        }

        private void ReconcilePlaybackLibrary()
        {
            var songs = new Dictionary<int, Music>(SongsSource.Count);
            foreach (var song in SongsSource) songs.TryAdd(song.Id, song);
            LibraryPlaybackReconciler.Reconcile(SequentialPlayingList, songs, CurrentPlayingMusic);
            if (!ReferenceEquals(SequentialPlayingList, CurrentPlayingList))
                LibraryPlaybackReconciler.Reconcile(CurrentPlayingList, songs, CurrentPlayingMusic);
        }

        /// <summary>
        /// UI-thread scan publication. Append only the new items; keep the existing viewport and selection.
        /// Sorting/grouping are reconciled once on completion (or when the user explicitly changes the view).
        /// </summary>
        public void AppendSongsBatch(IReadOnlyList<Music> batch)
        {
            if (batch.Count == 0) return;
            SongsSource.AddRange(batch);
            _libraryQueries.Invalidate();
            LibraryEmptyVisibility = Visibility.Collapsed;
            foreach (var music in batch)
            {
                // WinUI ListView does not support multi-item Add notifications. Bounded per-item Add
                // avoids Reset/re-sorting the entire library every 500 ms and preserves realized rows.
                if (string.IsNullOrWhiteSpace(SearchText) || LibraryProjectionService.MusicMatchesSearch(music, SearchText))
                    ListSongs.Add(music);
            }
        }

        public void RemoveFromSongsSource(Music music)
        {
            SongsSource.Remove(music);
            _libraryQueries.Invalidate();
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
            UsbMenusChanged?.Invoke();
            App.Services.GetRequiredService<SongListViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<PlaylistDetailViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<AlbumViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<ArtistViewModel>().UpDateUsbDeviceMenuflyout();
            App.Services.GetRequiredService<FolderViewModel>().UpDateUsbDeviceMenuflyout();
        }


        public void RefreshUsbDeviceMusicList()
        {
            var usbMusicGroups = UsbDeviceService.MusicOnDevice.AsValueEnumerable()
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

        public Task TransmitFileToUsb(IEnumerable<Music> selectedMusics, UsbStorageDevice usbDevice, string? format = null, int bitRateKbps = 320)
            => App.Services.GetRequiredService<UsbExportCoordinator>().ExportAsync(selectedMusics, usbDevice, format, bitRateKbps);

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

        /// <summary>先封闭入口，再等轮询实际退出；在 IPC/窗口释放前调用。</summary>
        public async Task StopAsync()
        {
            Dispose();
            await _playbackProgress.StopAsync();
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                StopProgressTimer();
                _searchDebounceTimer?.Stop();
                _settingsDebounceTimer?.Stop();
                GlobalHotKeyHook.ConflictsChanged -= OnGlobalHotKeyConflictsChanged;
                try
                {
                    if (App.MainWindow is { } window) GlobalHotKeyHook.ClearAll(window);
                }
                catch (Exception ex) { _logger.LogError(ex, "注销全局快捷键失败"); }
                if (_searchDebounceTimer is not null) _searchDebounceTimer.Tick -= OnSearchDebounceElapsed;
                _licenseService.StateChanged -= OnLicenseStateChanged;
                UsbDeviceService.DevicesChanged -= OnUsbDevicesChanged;
                UsbDeviceService.DeviceMusicChanged -= OnUsbMusicChanged;
                AllPlayList.CollectionChanged -= AllPlayList_CollectionChanged;
                FavoriteSongs.CollectionChanged -= FavoriteSongs_CollectionChanged;
                LyricsSyncRequestBus.Requested -= SendFullLyricsSync;
                State.DesktopLyrics.PropertyChanged -= OnDesktopLyricsStateChanged;
                State.Queue.PropertyChanged -= OnQueueStateChanged;
                State.Playback.PropertyChanged -= OnPlaybackStateChanged;
                State.Preferences.PropertyChanged -= OnPreferencesStateChanged;
                State.Browse.PropertyChanged -= OnBrowseStateChanged;
                State.Output.PropertyChanged -= OnPreferencesStateChanged;
            }
        }

        private DispatcherQueueTimer? _settingsDebounceTimer;

        internal void ScheduleSettingsBroadcast()
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
                // 固定字号模式不随竖屏放大；保留用户设置，只缩放渲染值。
                lyricsBlurAmount: LyricsBlurAmount * (IsPortraitLayout && !IsGlobalFontSizeEnabled ? PortraitLyricsScale : 1.0),
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

        internal void SendLyricsFontSize()
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
            AnimatedWin2dControls.Messages.TimeProgressBus.Publish((long)CurrentPlayingTime.TotalMilliseconds);
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
            ProgressSlider = newPosMs / 1000.0;
            App.Services.GetRequiredService<PlaybackCommands>().SeekCommand.Execute(newPosMs);
            IsManualSelect = false;
        }
    }
}
