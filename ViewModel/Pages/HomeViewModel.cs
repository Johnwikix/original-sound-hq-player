using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Recommendations;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>展示规范 Music 引用和推荐理由，所有操作转发到首页命令。</summary>
public sealed record HomeSong(Music Music, string Reason, IReadOnlyList<Music> Queue,
    IAsyncRelayCommand<HomeSong> PlayCommand);

/// <summary>保存精选轮播的主题和固定播放快照。</summary>
public sealed record HomeTheme(string Title, string Description, Music Artwork, IReadOnlyList<Music> Queue,
    IAsyncRelayCommand<HomeTheme> PlayCommand);

/// <summary>拥有首页快照、命令和显式生命周期；后台计算不持有视图或可变曲库。</summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private readonly MusicDatabaseService _database;
    private readonly PlaybackStatsService _statistics;
    private readonly HomeRecommendationService _recommendations;
    private readonly PlaybackCoordinator _playback;
    private readonly AddFolderViewModel _folders;
    private readonly WebDavLibraryService _remote;
    private readonly ILogger<HomeViewModel> _logger;
    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _operation;
    private Task _refreshTask = Task.CompletedTask;
    private bool _started, _active, _stopped, _running, _deferredRefresh, _dirty = true, _historyDirty = true;
    private long _revision, _snapshotVersion = -1;
    private int _batch;
    private DateTime _historyDay, _publishedDay;
    private RecommendationTrack[] _snapshot = [];
    private List<HomeListening> _history = [];
    private INotifyCollectionChanged? _favoriteSource;

    /// <summary>获取主题快照；列表整体替换，不修改已经播放的队列。</summary>
    public List<HomeTheme> Themes { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>获取通用推荐栏目。</summary>
    public List<HomeSong> Songs { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>获取近期有效收听的回顾栏目。</summary>
    public List<HomeSong> Familiar { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>获取有真实流派标签的探索栏目。</summary>
    public List<HomeSong> Discovery { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>获取首页主栏目标题。</summary>
    public string SongsTitle { get; private set => SetProperty(ref field, value); } = ToolUtils.GetString("HomeExplore");
    /// <summary>获取加载状态。</summary>
    public bool IsLoading { get; private set => SetProperty(ref field, value); } = true;
    /// <summary>获取确认加载后的空库状态。</summary>
    public bool IsEmpty { get; private set => SetProperty(ref field, value); }
    /// <summary>获取有库但没有可推荐候选的状态。</summary>
    public bool IsUnavailable { get; private set => SetProperty(ref field, value); }
    /// <summary>获取是否显示已有推荐内容。</summary>
    public bool HasContent => Songs.Count > 0;
    /// <summary>获取错误说明；已有内容在刷新失败时保留。</summary>
    public string ErrorMessage { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>获取换一批与重试命令。</summary>
    public IRelayCommand RefreshCommand { get; }
    /// <summary>获取显式建立栏目播放队列的命令。</summary>
    public IAsyncRelayCommand<HomeSong> PlaySongCommand { get; }
    /// <summary>获取播放精选主题的命令。</summary>
    public IAsyncRelayCommand<HomeTheme> PlayThemeCommand { get; }
    /// <summary>获取复用现有导入流程的命令。</summary>
    public IAsyncRelayCommand AddFolderCommand => _folders.AddFolderCommand;
    /// <summary>获取扫描状态，供空库反馈使用。</summary>
    public bool IsScanning => _folders.IsScanning;

    /// <summary>初始化依赖，不订阅事件或启动工作。</summary>
    public HomeViewModel(AppViewModel app, MusicDatabaseService database, PlaybackStatsService statistics,
        HomeRecommendationService recommendations, PlaybackCoordinator playback, AddFolderViewModel folders,
        WebDavLibraryService remote, ILogger<HomeViewModel> logger)
    {
        _app = app;
        _database = database;
        _statistics = statistics;
        _recommendations = recommendations;
        _playback = playback;
        _folders = folders;
        _remote = remote;
        _logger = logger;
        RefreshCommand = new RelayCommand(() => { _batch++; RequestRefresh(invalidateSnapshot: true); }, () => !_stopped);
        PlaySongCommand = new AsyncRelayCommand<HomeSong>(song => PlayAsync(song?.Music, song?.Queue),
            song => song is not null && CanPlay, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlayThemeCommand = new AsyncRelayCommand<HomeTheme>(theme => PlayAsync(theme?.Artwork, theme?.Queue),
            theme => theme is not null && CanPlay, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    private bool CanPlay => _active && !_stopped && _app.CanStartPlayback;

    /// <summary>开始接收失效信号；重复调用不会重复订阅。</summary>
    public void Start(DispatcherQueue dispatcher)
    {
        if (_started || _stopped) return;
        _started = true;
        _dispatcher = dispatcher;
        _app.SongsSourceChanged += LibraryChanged;
        _app.PropertyChanged += AppChanged;
        _app.State.Preferences.PropertyChanged += PreferencesChanged;
        ObserveFavorites();
        _app.State.Lifecycle.Changed += LifecycleChanged;
        _statistics.StatsUpdated += HistoryChanged;
        _folders.PropertyChanged += FoldersChanged;
        _remote.SourceAvailabilityChanged += SourceChanged;
    }

    /// <summary>激活页面，在数据已过期或跨日时刷新。</summary>
    public void Activate(DispatcherQueue dispatcher)
    {
        Start(dispatcher);
        if (_stopped) return;
        _active = true;
        NotifyCommands();
        if (_deferredRefresh) _dirty = true;
        if (_publishedDay != DateTime.UtcNow.Date || _snapshotVersion != _app.State.Library.Version) _dirty = true;
        if (_dirty) RequestRefresh();
    }

    /// <summary>停止页面消费并取消本次计算；下次激活可以恢复。</summary>
    public void Deactivate()
    {
        _active = false;
        _revision++;
        _operation?.Cancel();
        NotifyCommands();
    }

    /// <summary>退出时解除订阅并等待真实在途查询和计算完成。</summary>
    public async Task StopAsync()
    {
        if (_stopped) { await _refreshTask; return; }
        _stopped = true;
        Deactivate();
        if (_started)
        {
            _app.SongsSourceChanged -= LibraryChanged;
            _app.PropertyChanged -= AppChanged;
            _app.State.Preferences.PropertyChanged -= PreferencesChanged;
            if (_favoriteSource is not null) _favoriteSource.CollectionChanged -= FavoritesChanged;
            _favoriteSource = null;
            _app.State.Lifecycle.Changed -= LifecycleChanged;
            _statistics.StatsUpdated -= HistoryChanged;
            _folders.PropertyChanged -= FoldersChanged;
            _remote.SourceAvailabilityChanged -= SourceChanged;
        }
        await _refreshTask;
        _snapshot = [];
        _history.Clear();
    }

    /// <summary>复用文件夹导入与扫描互斥规则。</summary>
    public Task DropFoldersAsync(IReadOnlyList<Windows.Storage.IStorageItem> folders) => _folders.DropFoldersAsync(folders);

    private void LibraryChanged() => RequestRefresh(invalidateSnapshot: true);
    private void FavoritesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateForNextVisit();
    private void InvalidateForNextVisit()
    {
        if (_stopped) return;
        _deferredRefresh = true;
        // 播放、收藏与会话结算只更新下一次推荐输入，不移动用户正在操作的卡片。
        // 独立于在途刷新的版本，避免结算通知使当前查询失效后立即再洗一遍。
    }
    private void ObserveFavorites()
    {
        if (_favoriteSource is not null) _favoriteSource.CollectionChanged -= FavoritesChanged;
        _favoriteSource = _app.FavoriteSongs;
        _favoriteSource.CollectionChanged += FavoritesChanged;
    }
    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "ArtistSplitSymbols") RequestRefresh(invalidateSnapshot: true);
    }
    private void SourceChanged(int source, bool offline) => PostInvalidate();
    private void HistoryChanged() => PostInvalidate(history: true);
    private void PostInvalidate(bool history = false)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            if (_stopped) return;
            if (history) _historyDirty = true;
            if (history) InvalidateForNextVisit();
            else RequestRefresh(invalidateSnapshot: true);
        });
    }
    private void LifecycleChanged(object? sender, EventArgs e)
    {
        if (_app.State.Lifecycle.Phase == AppPhase.Stopping) Deactivate();
        else NotifyCommands();
    }
    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.IsPlaybackEngineReady) or nameof(AppViewModel.IsInitialized)) NotifyCommands();
        if (e.PropertyName == nameof(AppViewModel.FavoriteSongs)) { ObserveFavorites(); InvalidateForNextVisit(); }
        if (e.PropertyName == nameof(AppViewModel.CurrentPlayingMusic)) InvalidateForNextVisit();
    }
    private void FoldersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddFolderViewModel.IsScanning)) OnPropertyChanged(nameof(IsScanning));
    }
    private void NotifyCommands()
    {
        PlaySongCommand.NotifyCanExecuteChanged();
        PlayThemeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }
    private void RequestRefresh(bool invalidateSnapshot = false)
    {
        if (_stopped) return;
        _revision++;
        _dirty = true;
        if (invalidateSnapshot || _deferredRefresh) _snapshotVersion = -1;
        _deferredRefresh = false;
        if (!_active || _running) return;
        _running = true;
        _refreshTask = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        await Task.Yield(); // 先登记任务，且在曲库通知推进版本之后读取快照。
        try
        {
            while (_active && !_stopped && _dirty)
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(_app.State.Lifecycle.StoppingToken);
                _operation = operation;
                var token = operation.Token;
                try
                {
                    await Task.Delay(120, token); // 合并扫描批次、标签和来源通知，队列最多保留一个最新请求。
                    _dirty = false;
                    long revision = _revision;
                    long version = _app.State.Library.Version;
                    if (_app.SongsSource.Count == 0)
                    {
                        IsEmpty = _app.LibraryEmptyVisibility == Visibility.Visible;
                        IsLoading = !IsEmpty;
                        IsUnavailable = false;
                        if (IsEmpty)
                        {
                            _snapshot = [];
                            _snapshotVersion = version;
                            _publishedDay = DateTime.UtcNow.Date;
                            ErrorMessage = "";
                            Publish(new([], [], [], true));
                        }
                        continue;
                    }
                    IsEmpty = false;
                    IsLoading = !HasContent;
                    var snapshot = _snapshot;
                    if (_snapshotVersion != version)
                    {
                        snapshot = new RecommendationTrack[_app.SongsSource.Count];
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            if (revision != _revision || version != _app.State.Library.Version) break;
                            var m = _app.SongsSource[i];
                            snapshot[i] = new(m.Id, ArtistHelper.GetArtistNames(m.Author), m.Album, m.AlbumArtist,
                                m.Genre, m.Year, m.IsFavorite, m.IsPlayable, m.PlayCount);
                            if (i % 512 == 511) await Task.Yield();
                        }
                        if (revision != _revision || version != _app.State.Library.Version) { _dirty = true; continue; }
                        _snapshot = snapshot;
                        _snapshotVersion = version;
                    }
                    DateTime now = DateTime.UtcNow;
                    if (_historyDirty || _historyDay != now.Date)
                    {
                        _historyDirty = false;
                        try { _history = await _database.GetHomeListeningAsync(now, token); }
                        catch { _historyDirty = true; throw; }
                        _historyDay = now.Date;
                    }
                    var history = _history;
                    int batch = _batch;
                    var result = await Task.Run(() => _recommendations.Build(snapshot, history, now, batch, token), token);
                    if (!_active || _stopped || token.IsCancellationRequested) continue;
                    if (revision != _revision || version != _app.State.Library.Version)
                    {
                        _dirty = true;
                        // 大批导入期间先展示仍在规范库中的首批结果；继续计算最新版本。
                        if (!HasContent)
                        {
                            Publish(result);
                            IsLoading = false;
                        }
                        continue;
                    }
                    Publish(result);
                    IsLoading = false;
                    IsUnavailable = !HasContent;
                    ErrorMessage = "";
                    _publishedDay = now.Date;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { _dirty = true; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "首页推荐计算失败");
                    if (_active && !_stopped)
                    {
                        IsLoading = false;
                        ErrorMessage = ToolUtils.GetString("HomeError");
                    }
                }
                finally { _operation = null; }
            }
        }
        finally { _running = false; }
    }

    private void Publish(HomeRecommendation result)
    {
        Songs = Resolve(result.Songs, Songs);
        Familiar = Resolve(result.Familiar, Familiar);
        Discovery = Resolve(result.Discovery, Discovery);
        SongsTitle = ToolUtils.GetString(result.IsColdStart ? "HomeExplore" : "HomeForYou");
        var themes = new List<HomeTheme>(5);
        AddTheme(themes, SongsTitle, ToolUtils.GetString("HomeFeaturedDescription"), Songs);
        AddTheme(themes, ToolUtils.GetString("HomeFamiliar"), ToolUtils.GetString("HomeFamiliarDescription"), Familiar);
        AddTheme(themes, ToolUtils.GetString("HomeDiscovery"), ToolUtils.GetString("HomeDiscoveryDescription"), Discovery);
        // 第 4/5 页复用「精选」「常听」的下一位封面：标题沿用栏目名、从该曲起播同栏目队列，凑满 Store 走马灯的 5 张。
        AddTheme(themes, SongsTitle, ToolUtils.GetString("HomeFeaturedDescription"), Songs);
        AddTheme(themes, ToolUtils.GetString("HomeFamiliar"), ToolUtils.GetString("HomeFamiliarDescription"), Familiar);
        bool sameThemes = themes.Count == Themes.Count;
        for (int i = 0; sameThemes && i < themes.Count; i++) sameThemes = themes[i] == Themes[i];
        if (!sameThemes) Themes = themes;
        OnPropertyChanged(nameof(HasContent));
    }
    private List<HomeSong> Resolve(IReadOnlyList<RecommendedSong> songs, List<HomeSong> current)
    {
        var queue = new List<Music>(songs.Count);
        foreach (var song in songs) if (_app.FindById(song.MusicId) is { IsPlayable: true } music) queue.Add(music);
        var result = new List<HomeSong>(queue.Count);
        foreach (var song in songs)
        {
            var music = _app.FindById(song.MusicId);
            if (music is not { IsPlayable: true }) continue;
            string key = song.Reason switch
            {
                RecommendationReason.Artist => "HomeReasonArtist",
                RecommendationReason.Genre => "HomeReasonGenre",
                RecommendationReason.Favorite => "HomeReasonFavorite",
                RecommendationReason.Familiar => "HomeReasonFamiliar",
                _ => "HomeReasonExplore"
            };
            string reason = ToolUtils.GetString(key);
            if (song.Detail.Length > 0) reason = string.Format(reason, song.Detail);
            result.Add(new(music, reason, queue, PlaySongCommand));
        }
        if (result.Count == current.Count)
        {
            bool same = true;
            for (int i = 0; i < result.Count; i++)
                if (!ReferenceEquals(result[i].Music, current[i].Music) || result[i].Reason != current[i].Reason) { same = false; break; }
            if (same) return current;
        }
        return result;
    }
    private void AddTheme(List<HomeTheme> themes, string title, string description, List<HomeSong> songs)
    {
        // 各栏目榜首可能重复（同一首高热度曲），跳过已用作封面的曲目保证每页封面不同。
        foreach (var song in songs)
        {
            if (UsesArtwork(themes, song.Music)) continue;
            themes.Add(new(title, description, song.Music, song.Queue, PlayThemeCommand));
            return;
        }
    }
    private static bool UsesArtwork(List<HomeTheme> themes, Music music)
    {
        foreach (var theme in themes) if (ReferenceEquals(theme.Artwork, music)) return true;
        return false;
    }
    private async Task PlayAsync(Music? selected, IReadOnlyList<Music>? songs)
    {
        if (!CanPlay || selected is null || songs is null) return;
        selected = _app.FindById(selected.Id);
        if (selected is null) { RequestRefresh(invalidateSnapshot: true); return; }
        var queue = new List<Music>(songs.Count);
        foreach (var song in songs) if (_app.FindById(song.Id) is { } music) queue.Add(music);
        if (queue.Count == 0 || !CanPlay) return;
        _app.SequentialPlayingList = new BulkObservableCollection<Music>(queue);
        try { await _playback.PlayAsync(selected); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "播放首页推荐失败");
            if (_active && !_stopped) ErrorMessage = ToolUtils.GetString("HomePlaybackError");
        }
    }
}
