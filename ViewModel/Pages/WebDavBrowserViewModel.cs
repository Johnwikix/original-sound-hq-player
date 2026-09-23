using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>远程目录树和本次浏览选曲的短生命周期状态。</summary>
public partial class WebDavBrowserViewModel : ObservableObject, IDisposable
{
    private readonly WebDavSource _source;
    private readonly WebDavLibraryService _library;
    private readonly WebDavTransport _transport;
    private readonly MusicDatabaseService _database;
    private readonly PlaybackCoordinator _playback;
    private readonly AppViewModel _app;
    private readonly CancellationTokenSource _stop;
    private readonly List<WebDavTreeItem> _selectedTracks = [];
    private readonly HashSet<Task> _loads = [];
    private WebDavConnection? _connection;
    private bool _disposed;

    public WebDavBrowserViewModel(WebDavSource source, WebDavLibraryService library, WebDavTransport transport,
        MusicDatabaseService database, PlaybackCoordinator playback, AppViewModel app)
    {
        _source = source;
        _library = library;
        _transport = transport;
        _database = database;
        _playback = playback;
        _app = app;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(library.StoppingToken);
        Root = new(WebDavTransport.NormalizeRoot(source.BaseUri).AbsolutePath, source.Name, true);
    }

    public WebDavTreeItem Root { get; }
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public int SelectedTrackCount { get; private set => SetProperty(ref field, value); }

    public Task LoadChildrenAsync(WebDavTreeItem folder)
    {
        if (_disposed || !folder.IsDirectory || folder.IsLoaded) return Task.CompletedTask;
        if (folder.LoadTask is { IsCompleted: false } pending) return pending;
        var work = LoadChildrenCoreAsync(folder);
        folder.LoadTask = work;
        _loads.Add(work);
        _ = ObserveLoadAsync(work);
        return work;
    }

    private async Task ObserveLoadAsync(Task work)
    {
        try { await work; }
        finally { _loads.Remove(work); }
    }

    private async Task LoadChildrenCoreAsync(WebDavTreeItem folder)
    {
        folder.IsLoading = true;
        try
        {
            _connection ??= _library.Connect(_source);
            Status = ToolUtils.GetString("WebDavConnecting");
            var children = new List<WebDavTreeItem>();
            await foreach (var entry in _transport.ListAsync(_connection, folder.Href, _stop.Token))
                children.Add(new(entry.Href, entry.Name, entry.IsDirectory));
            _stop.Token.ThrowIfCancellationRequested();
            foreach (var child in children) folder.Children.Add(child);
            folder.IsLoaded = true;
            Status = ToolUtils.GetString("WebDavBrowserHint");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed");
        }
        finally { folder.IsLoading = false; }
    }

    public void UpdateSelection(IReadOnlyList<WebDavTreeItem> removed, IReadOnlyList<WebDavTreeItem> added)
    {
        foreach (var item in removed)
        {
            item.IsSelected = false;
            if (!item.IsDirectory) _selectedTracks.Remove(item);
        }
        foreach (var item in added)
        {
            item.IsSelected = true;
            if (!item.IsDirectory && !_selectedTracks.Contains(item)) _selectedTracks.Add(item);
        }
        SelectedTrackCount = _selectedTracks.Count;
        PlaySelectedCommand.NotifyCanExecuteChanged();
    }

    public async Task PlayItemAsync(WebDavTreeItem item)
    {
        if (_disposed || item.IsDirectory) return;
        try
        {
            var music = await _database.GetRemoteMusicAsync(_source.Id, item.Href);
            if (_disposed) return;
            if (music is null) { Status = ToolUtils.GetString("WebDavIndexFirst"); return; }
            await _playback.PlayAsync(music);
        }
        catch (Exception ex)
        {
            if (!_disposed) Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed");
        }
    }

    private bool CanPlaySelected() => !_disposed && _selectedTracks.Count != 0;

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    private async Task PlaySelectedAsync()
    {
        try
        {
            var selection = _selectedTracks.ToArray();
            var songs = new List<Music>(selection.Length);
            foreach (var item in selection)
            {
                if (_disposed || _stop.IsCancellationRequested) return;
                if (await _database.GetRemoteMusicAsync(_source.Id, item.Href) is { } music) songs.Add(music);
            }
            if (_disposed || !_app.CanStartPlayback) return;
            if (songs.Count == 0) { Status = ToolUtils.GetString("WebDavIndexFirst"); return; }
            // 用户显式指定的顺序构成独立队列，后续“下一首”沿这条队列播放。
            _app.CurrentPlayMode = ToolUtils.PlayMode.ListLoop;
            _app.SequentialPlayingList = new(songs);
            await _playback.PlayAtAsync(0);
            if (!_disposed && songs.Count != selection.Length) Status = ToolUtils.GetString("WebDavIndexFirst");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) Status = WebDavText.Error(ex is WebDavException dav ? dav.Code : "ConnectionFailed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        PlaySelectedCommand.NotifyCanExecuteChanged();
        _ = DrainAsync([.. _loads]);
    }

    private async Task DrainAsync(Task[] loads)
    {
        try { await Task.WhenAll(loads); }
        catch (OperationCanceledException) { }
        finally { _stop.Dispose(); }
    }
}
