using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>独立目录浏览页的会话状态；离开页面后取消读取并解绑曲库通知。</summary>
public partial class FolderViewModel : ObservableObject
{
    private readonly MusicDatabaseService _database;
    private readonly LibraryProjectionService _projections;
    private readonly ILogger<FolderViewModel> _logger;
    private readonly FolderBrowserIndex _index = new();
    private CancellationTokenSource? _read;
    private bool _active;
    private bool _stopped;
    private bool _refreshPending;
    private Task _loadTask = Task.CompletedTask;
    private Task _songsTask = Task.CompletedTask;
    private int _navigationRevision;
    private List<Folder> _roots = [];

    public AppViewModel AppViewModel { get; }
    public MusicBrowseViewModel MusicBrowseViewModel { get; }
    public BulkObservableCollection<FolderBrowserNode> Roots { get; } = [];
    public BulkObservableCollection<FolderBrowserNode> VisibleNodes { get; } = [];
    public FolderBrowserNode? SelectedNode { get; private set => SetProperty(ref field, value); }
    public string Title { get; private set => SetProperty(ref field, value); } = ToolUtils.GetString("FolderBrowserTitle");
    public string CurrentPath { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string Summary { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string Error { get; private set => SetProperty(ref field, value); } = string.Empty;
    public bool IsLoading { get; private set => SetProperty(ref field, value); }
    public bool IsLoadingSongs { get; private set => SetProperty(ref field, value); }
    public bool IsBusy => IsLoading || IsLoadingSongs;
    public bool IsEmpty => !IsBusy && Error.Length == 0 && AppViewModel.FolderSongs.Count == 0;
    public bool ShowAddFolder => Roots.Count == 0;
    public bool HasSongs => !IsBusy && Error.Length == 0 && AppViewModel.FolderSongs.Count > 0;
    // 会话内保留分栏比例；GridSplitter 的两列通过双向绑定共享同一份布局状态。
    public GridLength FolderColumnWidth { get; set => SetProperty(ref field, value); } = new(1, GridUnitType.Star);
    public GridLength SongsColumnWidth { get; set => SetProperty(ref field, value); } = new(3, GridUnitType.Star);
    public string ResizePanesLabel => ToolUtils.GetString("FolderBrowserResizePanes");
    public string PageTitle => ToolUtils.GetString("FolderBrowserTitle");
    public string EmptyTitle => ToolUtils.GetString("FolderBrowserEmpty");
    public string EmptyHint => ToolUtils.GetString("FolderBrowserEmptyHint");
    public string RetryLabel => ToolUtils.GetString("FolderBrowserRetry");

    public FolderViewModel(MusicBrowseViewModel musicBrowseViewModel, AppViewModel appViewModel,
        MusicDatabaseService database, LibraryProjectionService projections, ILogger<FolderViewModel> logger)
    {
        MusicBrowseViewModel = musicBrowseViewModel;
        AppViewModel = appViewModel;
        _database = database;
        _projections = projections;
        _logger = logger;
    }

    public Task ActivateAsync()
    {
        if (_stopped || _active) return _loadTask;
        _active = true;
        AppData.CurrentPage = typeof(View.FolderBrowsePage);
        AppViewModel.PageType = "folder";
        AppViewModel.IsBackBtnEnable = false;
        _projections.RefreshRequested += LibraryChanged;
        AppViewModel.State.Browse.PropertyChanged += BrowseChanged;
        AppViewModel.FolderSongs.CollectionChanged += SongsChanged;
        IsLoading = true;
        Error = string.Empty;
        PublishAvailability();
        RequestRefresh();
        return _loadTask;
    }

    private void RequestRefresh()
    {
        if (!_active || _stopped) return;
        _refreshPending = true;
        if (_read is { IsCancellationRequested: false }) return;
        var request = new CancellationTokenSource();
        _read = request;
        // 新会话等待上一次读取收尾；关闭时等待此链，取消不代表 SQLite 已停止。
        _loadTask = LoadAsync(_loadTask, request);
    }

    private async Task LoadAsync(Task previous, CancellationTokenSource request)
    {
        try
        {
            await previous;
            while (_active && !request.IsCancellationRequested && _refreshPending)
            {
                _refreshPending = false;
                // 只读扫描根表；歌曲数由内存索引统计，不反复查询全库计数。
                var roots = await _database.GetFolders();
                if (!_active || request.IsCancellationRequested || !ReferenceEquals(_read, request)) return;
                _roots = roots;
                Rebuild();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载目录浏览器失败");
            if (_active && !request.IsCancellationRequested) Error = ToolUtils.GetString("FolderBrowserLoadError");
        }
        finally
        {
            if (ReferenceEquals(_read, request))
            {
                _read = null;
                IsLoading = false;
                PublishAvailability();
            }
            request.Dispose();
        }
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _navigationRevision++;
        IsLoadingSongs = false;
        _read?.Cancel();
        _projections.RefreshRequested -= LibraryChanged;
        AppViewModel.State.Browse.PropertyChanged -= BrowseChanged;
        AppViewModel.FolderSongs.CollectionChanged -= SongsChanged;
    }

    public async Task StopAsync()
    {
        _stopped = true;
        Deactivate();
        await Task.WhenAll(_loadTask, _songsTask);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        Deactivate();
        await ActivateAsync();
    }

    private void LibraryChanged() => RequestRefresh();
    private void BrowseChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppViewModel.State.Browse.SourceFilterId)) Rebuild();
    }
    private void Rebuild()
    {
        if (!_active) return;
        var state = AppViewModel.State;
        var expanded = _index.GetExpandedLocations();
        var previousNode = SelectedNode;
        _index.Rebuild(AppViewModel.SongsSource, _roots, state.Browse.SourceFilterId);
        foreach (var location in expanded)
            if (_index.Find(location) is { } expandedNode)
                expandedNode.IsExpanded = true;
        var selected = _index.Find(state.Browse.CurrentFolder);
        // 当前目录被移除时退到仍存在的最近祖先。
        for (var ancestor = previousNode?.Parent; selected is null && ancestor is not null; ancestor = ancestor.Parent)
            selected = _index.Find(ancestor.Location);
        for (var ancestor = selected?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            ancestor.IsExpanded = true;
        Roots.FillFrom(CollectionsMarshal.AsSpan(_index.Roots));
        RebuildVisibleNodes();
        Navigate(selected ?? (Roots.Count > 0 ? Roots[0] : null));
    }

    [RelayCommand]
    private void SelectFolder(FolderBrowserNode? node)
    {
        var current = _index.Find(node?.Location);
        if (current is not null && !ReferenceEquals(current, SelectedNode)) Navigate(current);
    }

    [RelayCommand(CanExecute = nameof(CanNavigateIntoFolder))]
    private void NavigateIntoFolder(FolderBrowserNode? node)
    {
        if (node is not { HasChildren: true }) return;
        if (node.IsExpanded) SelectFolder(node.Children[0]);
        else ExpandFolder(node);
    }

    private static bool CanNavigateIntoFolder(FolderBrowserNode? node) => node?.HasChildren == true;

    [RelayCommand(CanExecute = nameof(CanNavigateOutOfFolder))]
    private void NavigateOutOfFolder(FolderBrowserNode? node)
    {
        if (node is null) return;
        if (node.IsExpanded) CollapseFolder(node);
        else if (node.Parent is not null) SelectFolder(node.Parent);
    }

    private static bool CanNavigateOutOfFolder(FolderBrowserNode? node)
        => node is not null && (node.IsExpanded || node.Parent is not null);

    public void ExpandFolder(FolderBrowserNode node)
    {
        if (node.IsExpanded || !node.HasChildren) return;
        node.IsExpanded = true;
        int index = VisibleNodes.IndexOf(node);
        if (index < 0) return;
        List<FolderBrowserNode> descendants = [];
        Stack<FolderBrowserNode> pending = new();
        PushChildren(node, pending);
        while (pending.TryPop(out var current))
        {
            descendants.Add(current);
            if (current.IsExpanded) PushChildren(current, pending);
        }
        VisibleNodes.InsertRangeWithItemNotifications(index + 1, descendants);
        // 收起会移除列表选择；只有选中项重新可见时才重新推送。
        if (SelectedNode is { } selected && VisibleNodes.Contains(selected))
            for (var current = selected; current is not null; current = current.Parent)
                if (ReferenceEquals(current, node))
                {
                    OnPropertyChanged(nameof(SelectedNode));
                    break;
                }
    }

    public void CollapseFolder(FolderBrowserNode node)
    {
        if (!node.IsExpanded) return;
        node.IsExpanded = false;
        int index = VisibleNodes.IndexOf(node);
        if (index < 0) return;
        int descendant = index + 1;
        while (descendant < VisibleNodes.Count && VisibleNodes[descendant].Depth > node.Depth) descendant++;
        VisibleNodes.RemoveRangeWithItemNotifications(index + 1, descendant - index - 1);
    }

    private void RebuildVisibleNodes()
    {
        List<FolderBrowserNode> rows = [];
        Stack<FolderBrowserNode> pending = new();
        for (int i = _index.Roots.Count - 1; i >= 0; i--) pending.Push(_index.Roots[i]);
        while (pending.TryPop(out var current))
        {
            rows.Add(current);
            if (current.IsExpanded) PushChildren(current, pending);
        }
        VisibleNodes.FillFrom(CollectionsMarshal.AsSpan(rows));
    }

    private static void PushChildren(FolderBrowserNode node, Stack<FolderBrowserNode> pending)
    {
        for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
    }

    private void Navigate(FolderBrowserNode? node)
    {
        if (!_active) return;
        SelectedNode = node;
        // 清除不同目录的可操作列表，防止异步排序期间播放到上一个目录。
        if (AppViewModel.State.Browse.CurrentFolder?.Identity != node?.Location.Identity)
            _projections.ClearSongCollection(AppViewModel.FolderSongs);
        AppViewModel.State.Browse.CurrentFolder = node?.Location;
        Title = node?.Name ?? PageTitle;
        CurrentPath = node?.Path ?? string.Empty;
        IsLoadingSongs = true;
        _songsTask = LoadSongsAsync(++_navigationRevision);
        PublishAvailability();
    }
    private async Task LoadSongsAsync(int revision)
    {
        try
        {
            await _projections.UpdateSongCollectionsAsync(AppViewModel.FolderSongs, SongViewType.Folder,
                music => AppViewModel.State.Browse.CurrentFolder?.Matches(music) == true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载当前目录歌曲失败");
            if (_active && revision == _navigationRevision) Error = ToolUtils.GetString("FolderBrowserLoadError");
        }
        finally
        {
            if (_active && revision == _navigationRevision)
            {
                IsLoadingSongs = false;
                PublishAvailability();
            }
        }
    }

    private void SongsChanged(object? sender, NotifyCollectionChangedEventArgs args) => PublishAvailability();
    private void PublishAvailability()
    {
        Summary = string.Format(ToolUtils.GetString("FolderBrowserSummary"), AppViewModel.FolderSongs.Count);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(ShowAddFolder));
    }
}
