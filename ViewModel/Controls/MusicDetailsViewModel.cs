using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>一次歌曲详情的展示、歌单操作与编辑会话；不持有页面或来源列表。</summary>
public sealed class MusicDetailsViewModel : ObservableObject
{
    private sealed record PlaylistTarget(int PlaylistId, int MusicId);
    private readonly AppViewModel _app;
    private readonly MusicDatabaseService _database;
    private readonly EditorSessions _sessions;
    private readonly ILogger _logger;
    private readonly IAsyncRelayCommand<PlaylistTarget> _add;
    private IDisposable? _session;
    private Task _playlistTask = Task.CompletedTask;
    private Task? _stopTask;
    private bool _started, _closing;
    public MusicDetailsEditorViewModel Editor { get; }
    public Music Music => Editor.Music;
    public string Reason { get; }
    public ICommand PlayCommand { get; }
    public object? PlayParameter { get; private set => SetProperty(ref field, value); }
    public List<MenuModel> PlaylistMenuItems { get; } = [];
    public IRelayCommand PreparePlaylistMenuCommand { get; }
    public string ErrorMessage { get; private set => SetProperty(ref field, value); } = "";
    public event EventHandler? CloseRequested;

    public MusicDetailsViewModel(MusicDetailsEditorViewModel editor, AppViewModel app,
        MusicDatabaseService database, EditorSessions sessions, ILogger logger,
        ICommand playCommand, object? playParameter, string reason = "")
    {
        Editor = editor;
        _app = app;
        _database = database;
        _sessions = sessions;
        _logger = logger;
        PlayCommand = playCommand;
        PlayParameter = playParameter;
        Reason = reason;
        PreparePlaylistMenuCommand = new RelayCommand(PrepareMenu);
        _add = new AsyncRelayCommand<PlaylistTarget>(EnqueueAdd, CanAdd, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    public void Start()
    {
        if (_started || _closing) return;
        _started = true;
        // 外层屏障持续到编辑器和已接受歌单写入真正完成，不因页面卸载提前注销。
        _session = _sessions.Attach(StopAsync);
        Editor.Saved += EditorSaved;
        _app.SongsSourceChanged += LibraryChanged;
        _app.PropertyChanged += AppChanged;
        Editor.Start();
    }

    private void EditorSaved(object? sender, EventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void LibraryChanged()
    {
        if (!_closing && Music.Id != 0 && !ReferenceEquals(_app.FindById(Music.Id), Music))
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private void AppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.IsPlaybackEngineReady) or nameof(AppViewModel.IsInitialized))
            (PlayCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
    private bool PlaylistExists(int id)
    {
        foreach (var item in _app.AllPlayList) if (item.Id == id) return true;
        return false;
    }
    private bool CanAdd(PlaylistTarget? target) => !_closing && _app.CanPublishState && target is not null
        && _app.FindById(target.MusicId) is not null && PlaylistExists(target.PlaylistId);

    private void PrepareMenu()
    {
        PlaylistMenuItems.Clear();
        if (_closing) return;
        foreach (var item in _app.AllPlayList)
            PlaylistMenuItems.Add(new MenuModel { Title = item.Name, Tag = new PlaylistTarget(item.Id, Music.Id), Command = _add });
        if (PlaylistMenuItems.Count == 0)
            PlaylistMenuItems.Add(new MenuModel { Title = ToolUtils.GetString("HomeNoPlaylists"), IsEnabled = false });
    }
    private Task EnqueueAdd(PlaylistTarget? target)
    {
        if (!CanAdd(target)) return Task.CompletedTask;
        return _playlistTask = AddAfterAsync(_playlistTask, target!);
    }
    private async Task AddAfterAsync(Task previous, PlaylistTarget target)
    {
        await previous;
        if (_app.FindById(target.MusicId) is null || !PlaylistExists(target.PlaylistId)) return;
        try
        {
            await _database.AddMusicToPlayList(target.PlaylistId, target.MusicId);
            if (!_closing) ErrorMessage = "";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "添加歌曲到歌单失败");
            if (!_closing) ErrorMessage = ToolUtils.GetString("HomePlaylistAddFailed");
        }
    }
    public void SetPlaybackParameter(MusicDetailsRequest request)
    {
        if (!_closing) PlayParameter = request;
    }

    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        Editor.Saved -= EditorSaved;
        _app.SongsSourceChanged -= LibraryChanged;
        _app.PropertyChanged -= AppChanged;
        CloseRequested = null;
        PlaylistMenuItems.Clear();
        _add.NotifyCanExecuteChanged();
        Editor.BeginClose();
    }
    public Task StopAsync() => _stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        BeginClose();
        try { await Task.WhenAll(_playlistTask, Editor.StopAsync()); }
        catch (Exception exception) { _logger.LogError(exception, "结束歌曲详情会话失败"); }
        finally
        {
            PlayParameter = null;
            PlaylistMenuItems.Clear();
            _session?.Dispose();
            _session = null;
        }
    }
}
