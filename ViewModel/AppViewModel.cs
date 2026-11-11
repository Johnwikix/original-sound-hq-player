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
        public Music? CurrentArtistObj { get => State.Browse.CurrentArtistObj; set => State.Browse.CurrentArtistObj = value; }
        public Music? CurrentAlbumObj { get => State.Browse.CurrentAlbumObj; set => State.Browse.CurrentAlbumObj = value; }
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

        public List<Music> SongsSource
        {
            get => State.Library.Songs;
            set
            {
                if (ReferenceEquals(State.Library.Songs, value)) return;
                State.Library.Replace(value);
            }
        }
        public BulkObservableCollection<Music> FavoriteSongs { get => State.LibraryViews.FavoriteSongs; set => State.LibraryViews.FavoriteSongs = value; }
        public BulkObservableCollection<PlayListMusicItem> PlayListSongs { get => State.LibraryViews.PlayListSongs; set => State.LibraryViews.PlayListSongs = value; }
        public BulkObservableCollection<PlayList> AllPlayList { get => State.LibraryViews.AllPlayList; set => State.LibraryViews.AllPlayList = value; }
        public PlayList CurrentPlayList { get => State.Browse.CurrentPlayList; set => State.Browse.CurrentPlayList = value; }
        public int CurrentPlayListId { get => State.Browse.CurrentPlayListId; set => State.Browse.CurrentPlayListId = value; }
        public CollectionViewSource AlbumPageSource { get => State.LibraryViews.AlbumPageSource; set => State.LibraryViews.AlbumPageSource = value; }
        public CollectionViewSource ArtistPageSource { get => State.LibraryViews.ArtistPageSource; set => State.LibraryViews.ArtistPageSource = value; }
        public BulkObservableCollection<Music> ListSongs { get => State.LibraryViews.ListSongs; set => State.LibraryViews.ListSongs = value; }
        public BulkObservableCollection<Music> AlbumSongs { get => State.LibraryViews.AlbumSongs; set => State.LibraryViews.AlbumSongs = value; }
        public BulkObservableCollection<Music> ArtistSongs { get => State.LibraryViews.ArtistSongs; set => State.LibraryViews.ArtistSongs = value; }
        public BulkObservableCollection<Music> FolderSongs { get => State.LibraryViews.FolderSongs; set => State.LibraryViews.FolderSongs = value; }
        public Music? CurrentPlayingMusic { get => State.Playback.CurrentPlayingMusic; set => State.Playback.CurrentPlayingMusic = value; }
        public Music? SelectedPlaybackMusic => State.Playback.PendingSelection?.Music ?? CurrentPlayingMusic;
        public int GetSelectedPlaybackIndex()
        {
            if (State.Playback.PendingSelection is { } pending)
            {
                for (int index = 0; index < CurrentPlayingList.Count; index++)
                    if (State.Queue.EntryIdAt(index) == pending.EntryId) return index;
                return -1;
            }
            return GetCurrentIndex();
        }
        public BulkObservableCollection<Music> SequentialPlayingList
        {
            get => State.Queue.Sequential;
            set { if (CanPublishState) State.Queue.Replace(value); }
        }
        /// <summary>以列表当前的来源范围建立队列；文件浏览入口同时固定每个出现项。</summary>
        public void SetBrowseQueue(BulkObservableCollection<Music> songs, bool exactFiles = false, int? sourceScope = null)
        {
            if (CanStartPlayback) State.Queue.Replace(songs, sourceScope ?? State.Browse.SourceFilterId, exactFiles);
        }

        public BulkObservableCollection<Music> CurrentPlayingList => State.Queue.Playing;
        public SortOption SelectedSortOption { get => State.Browse.SelectedSortOption; set => State.Browse.SelectedSortOption = value; }

        public ObservableCollection<SortOption> SortOptions { get; set => SetProperty(ref field, value); } = [
            new SortOption( "DefaultOrder", "SortOrderDefault"),
            new SortOption("A-Z", "SortOrderA_Z"),
            new SortOption("Artist", "SortOrderArtist"),
            new SortOption("Album", "SortOrderAlbum"),
            new SortOption("AlbumArtist", "SortOrderAlbumArtist"),
            new SortOption("Genre", "SortOrderGenre"),
            new SortOption("YearASC", "SortOrderYearASC"),
            new SortOption("YearDESC", "SortOrderYearDESC"),
            new SortOption("CreateTimeASC", "SortOrderCreateTimeASC"),
            new SortOption("CreateTimeDESC", "SortOrderCreateTimeDESC"),
            new SortOption("UpdateTimeASC", "SortOrderUpdateTimeASC"),
            new SortOption("UpdateTimeDESC", "SortOrderUpdateTimeDESC")
        ];
        public string MusicInfo { get => State.Presentation.MusicInfo; set => State.Presentation.MusicInfo = value; }
        public bool IsMuted { get; set; } = false;
        public double TempVolume { get; set; } = 50;
        public string PlayTimeText { get => State.Playback.PlayTimeText; set => State.Playback.PlayTimeText = value; }
        public string ElapsedTimeText => State.Playback.ElapsedTimeText;
        public string RemainingTimeText => State.Playback.RemainingTimeText;
        //public string ProgressSliderThumbTipText { get; set => SetProperty(ref field, value); } = "00:00";
        public double ProgressSliderMax { get => State.Playback.ProgressSliderMax; set => State.Playback.ProgressSliderMax = value; }
        public List<LyricLine> UILyrics { get => State.Presentation.UILyrics; set => State.Presentation.UILyrics = value; }
        public int LastLyricIndex { get => State.Presentation.LastLyricIndex; set => State.Presentation.LastLyricIndex = value; }
        public string LyricPageBackgroundHash { get => State.Presentation.LyricPageBackgroundHash; set => State.Presentation.LyricPageBackgroundHash = value; }
        public AnimatedWin2dControls.Impressionist.PaletteResult? LyricPagePalette { get => State.Presentation.LyricPagePalette; set => State.Presentation.LyricPagePalette = value; }
        // 当前曲目封面像素（RotatingMesh 背景着色器消费；null 表示无封面，着色器回退调色板渐变）。
        public AnimatedWin2dControls.Impressionist.ArtworkPixelData? LyricPageArtwork { get => State.Presentation.LyricPageArtwork; set => State.Presentation.LyricPageArtwork = value; }
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
        public Visibility LibraryEmptyVisibility { get => State.LibraryViews.LibraryEmptyVisibility; set => State.LibraryViews.LibraryEmptyVisibility = value; }
        // 最爱页占位：库非空但没有任何收藏时显示（FavouritePlayListPage）；搜索过滤导致的空列表不显示。
        public Visibility FavoriteEmptyVisibility { get => State.LibraryViews.FavoriteEmptyVisibility; set => State.LibraryViews.FavoriteEmptyVisibility = value; }
        public bool IsFullScreen { get => State.Shell.IsFullScreen; set => State.Shell.IsFullScreen = value; }
        public bool IsMaximized { get => State.Shell.IsMaximized; set => State.Shell.IsMaximized = value; }
        public bool IsPointerOverTitleBar { get => State.Shell.IsPointerOverTitleBar; set => State.Shell.IsPointerOverTitleBar = value; }
        public bool IsPlayingDetailVisible { get => State.DesktopLyrics.IsPlayingDetailVisible; set => State.DesktopLyrics.IsPlayingDetailVisible = value; }
        public void ToggleFullScreen() => IsFullScreen = !IsFullScreen;
        public void UpdateMaximizeState() => App.Services.GetRequiredService<ShellService>().UpdateMaximizeState();
        public void SyncFullScreenStateFromWindow() => App.Services.GetRequiredService<ShellService>().SyncFullScreenStateFromWindow();
        public string InfoBarTitle { get => State.Shell.InfoBarTitle; set => State.Shell.InfoBarTitle = value; }
        public bool InfoBarIsOpen { get => State.Shell.InfoBarIsOpen; set => State.Shell.InfoBarIsOpen = value; }
        public string InfoBarMessage { get => State.Shell.InfoBarMessage; set => State.Shell.InfoBarMessage = value; }
        public string PageType { get; set; } = string.Empty;
        public float ControlsStackOpacity { get; set => SetProperty(ref field, value); } = 0.0f;
        public bool IsBackBtnEnable { get; set => SetProperty(ref field, value); } = false;
        public TimeSpan LyricsDurationTime { get => State.Presentation.LyricsDurationTime; set => State.Presentation.LyricsDurationTime = value; }
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
        public double Volume { get => State.Playback.Volume; set => State.Playback.Volume = value; }

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
            State.Presentation.PropertyChanged += OnPreferencesStateChanged;
            State.Preferences.PropertyChanged += OnPreferencesStateChanged;
            State.Browse.PropertyChanged += OnBrowseStateChanged;
            State.Output.PropertyChanged += OnPreferencesStateChanged;
            State.HotKeys.PropertyChanged += OnPreferencesStateChanged;
            State.Shell.PropertyChanged += OnPreferencesStateChanged;
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
            State.LibraryViews.PropertyChanged += OnPreferencesStateChanged;
        }

        private void OnBrowseStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            _appendedSongIds = null;
            OnPropertyChanged(e);
        }

        private void OnPlaybackStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
            if (e.PropertyName is nameof(State.Playback.PendingSelection) or nameof(CurrentPlayingMusic))
                OnPropertyChanged(nameof(SelectedPlaybackMusic));
            if (e.PropertyName == nameof(Volume) && IsInitialized)
            {
                if (Volume > 0) IsMuted = false;
                if (!IsMuted) TempVolume = Volume;
                App.Services.GetRequiredService<BassPlayerCommandService>().SetVolume(Volume / 100);
            }
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

        private void OnPreferencesStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
            string? computed = e.PropertyName switch
            {
                nameof(PaletteAlgorithm) => nameof(PaletteAlgorithmIndex),
                nameof(BackgroundShader) => nameof(BackgroundShaderIndex),
                nameof(ScrollEasingType) => nameof(ScrollEasingTypeIndex),
                nameof(ScrollEasingMode) => nameof(ScrollEasingModeIndex),
                nameof(PlayingDetailAlignment) or nameof(UsePlayingDetailAlignmentInPortrait) or nameof(IsPortraitLayout) => nameof(EffectivePlayingDetailAlignment),
                nameof(AtmosEndpointId) => nameof(SelectedAtmosDevice),
                _ => null
            };
            if (computed is not null) OnPropertyChanged(computed);
        }

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
            if (e.PropertyName == nameof(IsPlayingDetailVisible)) OnPropertyChanged(e);
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

        public void LoadLyricsToUI(Music music) => App.Services.GetRequiredService<LyricsLoader>().Load(music);

        public void AdjustVolume(int delta)
        {
            double newVolume = Volume + delta;
            newVolume = Math.Max(0, Math.Min(newVolume, 100));
            Volume = newVolume;
        }

        public Music? FindById(int id) => _libraryQueries.FindById(id);
        public bool TryFindById(int id, out Music? music) => _libraryQueries.TryFindById(id, out music);
        /// <summary>获取同歌曲的文件版本索引，供播放时解析。</summary>
        internal IReadOnlyList<Music>? FindMultipleVersions(int songId) => _libraryQueries.GetMultipleVersions(songId);
        public IReadOnlyList<Music> FindVersions(int songId) => _libraryQueries.GetVersions(songId);
        /// <summary>返回列表使用的稳定代表文件。</summary>
        public Music? FindDisplayVersion(int songId, int sourceId = -1) => _libraryQueries.FindDisplayVersion(songId, sourceId);
        public Music? FindFirstByAlbum(string? album) => _libraryQueries.FindFirstByAlbum(album);
        public Music? FindFirstByArtist(string? artist) => _libraryQueries.FindFirstByArtist(artist);
        public int GetAlbumSongCount(string? album) => _libraryQueries.GetAlbumSongCount(album);
        internal IReadOnlyList<PlayListMusicItem> GetPlaylistItems(int playlistId, string? search, int scope) =>
            _projections.GetPlaylistItems(playlistId, search, scope);

        public void NotifyIdIndexChanged() => _libraryQueries.Invalidate();

        public event Action? SongsSourceChanged;
        /// <summary>在 UI 线程发布共享收藏投影，不触发旧数据库回读覆盖新意图。</summary>
        public event Action? SharedFavoriteChanged;
        /// <summary>通知已打开详情同步共享收藏。</summary>
        public void NotifySharedFavoriteChanged() => SharedFavoriteChanged?.Invoke();

        public void NotifySongsSourceChanged() => PublishSongsSourceChanged(reconcilePlayback: false);

        private void PublishSongsSourceChanged(bool reconcilePlayback)
        {
            _appendedSongIds = null;
            _libraryQueries.Invalidate();
            if (reconcilePlayback) ReconcilePlaybackLibrary();
            SongsSourceChanged?.Invoke();
            RefreshPlayListSummaries();
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
            RefreshPlayListSummaries();
            UpdateMenuOptionsPlayList();
        }

        internal void RefreshPlayListSummaries()
            => PlaylistSummaryProjection.Refresh(AllPlayList, AppData.AllPlayListMusics, _libraryQueries);

        public event Action? PlaylistMenusChanged;
        public event Action? UsbMenusChanged;

        public void UpdateMenuOptionsPlayList() => PlaylistMenusChanged?.Invoke();

        public Task UpdateSongCollectionsAsync(BulkObservableCollection<Music> target, SongViewType kind, Func<Music, bool>? filter = null)
            => _projections.UpdateSongCollectionsAsync(target, kind, filter);
        public void UpdateGroupedByFirstLetter(Func<Music, string> distinct, Func<Music, string> group, CollectionViewSource source)
            => _projections.UpdateGroupedByFirstLetter(distinct, group, source);
        public void UpdateArtistGroupedByFirstLetter(CollectionViewSource source)
            => _projections.UpdateArtistGroupedByFirstLetter(source);

        public void AddMusicToCurrentPlayList(Music music, bool exactFiles = false)
        {
            if (music is not null) State.Queue.InsertNext(CurrentPlayingMusic, [music], State.Browse.SourceFilterId, exactFiles);
        }

        public void AddMusicRangeToCurrentPlayList(IEnumerable<Music> musics, bool exactFiles = false)
        {
            if (musics is not null) State.Queue.InsertNext(CurrentPlayingMusic, musics, State.Browse.SourceFilterId, exactFiles);
        }

        public int GetCurrentIndex() => State.Queue.IndexOf(CurrentPlayingMusic);

        public void RefreshDataSource() => App.Services.GetRequiredService<LibraryBrowseCoordinator>().RefreshDataSource();
        public void RefreshAllViews() => App.Services.GetRequiredService<LibraryBrowseCoordinator>().RefreshAllViews();
        public Task RefreshPlayListSongMapping() => _projections.RefreshPlayListSongMapping(PlayListSongs);
        public void OnSelectSortChanged() => RefreshDataSource();

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

        public Task RefreshSongsSourceAsync(CancellationToken cancellationToken = default)
            => _musicDatabaseService.PublishLibrarySnapshotAsync(cancellationToken);

        internal void ApplyLibrarySnapshot(IReadOnlyList<Music> songs)
        {
            SongsSource.Clear();
            SongsSource.AddRange(songs);
            PublishSongsSourceChanged(reconcilePlayback: true);
        }

        internal void ApplyVersionState(int musicId, IReadOnlyList<Music> members)
        {
            bool retained = false;
            foreach (var member in members)
            {
                if (member.Id == musicId) retained = true;
                if (FindById(member.Id) is not { } file) continue;
                file.IsMissing = member.IsMissing;
                file.SongPreferredMusicId = member.SongPreferredMusicId;
            }
            if (!retained && FindById(musicId) is { } removed)
            {
                removed.IsDetached = true;
                SongsSource.Remove(removed);
            }
            PublishSongsSourceChanged(reconcilePlayback: true);
        }

        private void ReconcilePlaybackLibrary()
        {
            if (CurrentPlayingMusic is null && State.Queue.Sequential.Count == 0) return;
            var songs = _libraryQueries.GetIdIndex();
            if (CurrentPlayingMusic is { } playing && songs.TryGetValue(playing.Id, out var refreshed))
            {
                // 保留音频会话与文件快照，仅同步已经提交的歌曲归属和共享状态。
                playing.IsDetached = refreshed.IsDetached;
                playing.IsMissing = refreshed.IsMissing;
                playing.SongId = refreshed.SongId;
                playing.SongPreferredMusicId = refreshed.SongPreferredMusicId;
                playing.IsFavorite = refreshed.IsFavorite;
                playing.Order = refreshed.Order;
                playing.HasVersionLyrics = refreshed.HasVersionLyrics;
            }
            else if (CurrentPlayingMusic is { Id: > 0 } detached)
            {
                detached.IsDetached = true;
            }
            State.Queue.ReconcileLibrary(songs, CurrentPlayingMusic, _libraryQueries.FindDisplayVersion);
        }

        private HashSet<int>? _appendedSongIds;

        /// <summary>
        /// UI-thread scan publication. Append only the new items; keep the existing viewport and selection.
        /// Sorting/grouping are reconciled once on completion (or when the user explicitly changes the view).
        /// </summary>
        public void AppendSongsBatch(IReadOnlyList<Music> batch)
        {
            if (batch.Count == 0) return;
            if (_appendedSongIds is null)
            {
                _appendedSongIds = new HashSet<int>();
                foreach (var existing in ListSongs)
                    if (existing.SongId > 0) _appendedSongIds.Add(existing.SongId);
            }
            SongsSource.AddRange(batch);
            _libraryQueries.Invalidate();
            LibraryEmptyVisibility = Visibility.Collapsed;
            foreach (var music in batch)
            {
                // WinUI ListView does not support multi-item Add notifications. Bounded per-item Add
                // avoids Reset/re-sorting the entire library every 500 ms and preserves realized rows.
                if (string.IsNullOrWhiteSpace(SearchText) || LibraryProjectionService.MusicMatchesSearch(music, SearchText))
                    if (music.SongId <= 0 || _appendedSongIds.Add(music.SongId)) ListSongs.Add(music);
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

        public void UpDateUsbDeviceMenuflyout() => UsbMenusChanged?.Invoke();

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

        public Task ReGetLyrics(IEnumerable<Music> songs, Music? selectedMusic = null)
            => App.Services.GetRequiredService<LibraryTrackActions>().RefreshLyricsAsync(songs);

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

        public Task RescanFolder(Music music) => App.Services.GetRequiredService<LibraryTrackActions>().RescanAsync(music);

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
                _licenseService.StateChanged -= OnLicenseStateChanged;
                UsbDeviceService.DevicesChanged -= OnUsbDevicesChanged;
                UsbDeviceService.DeviceMusicChanged -= OnUsbMusicChanged;
                AllPlayList.CollectionChanged -= AllPlayList_CollectionChanged;
                State.LibraryViews.PropertyChanged -= OnPreferencesStateChanged;
                State.DesktopLyrics.PropertyChanged -= OnDesktopLyricsStateChanged;
                State.Queue.PropertyChanged -= OnQueueStateChanged;
                State.Playback.PropertyChanged -= OnPlaybackStateChanged;
                State.Presentation.PropertyChanged -= OnPreferencesStateChanged;
                State.Preferences.PropertyChanged -= OnPreferencesStateChanged;
                State.Browse.PropertyChanged -= OnBrowseStateChanged;
                State.Output.PropertyChanged -= OnPreferencesStateChanged;
                State.HotKeys.PropertyChanged -= OnPreferencesStateChanged;
                State.Shell.PropertyChanged -= OnPreferencesStateChanged;
            }
        }

        internal void ScheduleSettingsBroadcast() => App.Services.GetRequiredService<LyricsPresentationService>().ScheduleSettingsBroadcast();
        public void SendLyricsSettings() => App.Services.GetRequiredService<LyricsPresentationService>().SendLyricsSettings();
        internal void SendLyricsFontSize() => App.Services.GetRequiredService<LyricsPresentationService>().SendLyricsFontSize();

        [RelayCommand]
        private void OnVolumeSliderIconButtonChanged()
        {
            IsMuted = !IsMuted;
            Volume = IsMuted ? 0 : TempVolume;
        }

        [RelayCommand]
        private void ToggleLyricsTranslation() => IsLyricsTranslationEnabled = !IsLyricsTranslationEnabled;

        [RelayCommand]
        private void ToggleLyricsPronunciation() => IsLyricsPronunciationEnabled = !IsLyricsPronunciationEnabled;

        [RelayCommand]
        private void OnFullScreenButtonChanged()
        {
            ToggleFullScreen();
        }

        [RelayCommand]
        private void OnStopButtonChanged()
        {
            App.Services.GetRequiredService<PlaybackCoordinator>().CancelPendingSelection();
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
