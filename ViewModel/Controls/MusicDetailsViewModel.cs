using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>拥有歌曲详情及按版本保留的编辑会话，不复制播放和收藏状态。</summary>
public sealed class MusicDetailsViewModel : ObservableObject
{
    private sealed record PlaylistTarget(int PlaylistId, int MusicId);
    private sealed record VersionSession(MusicDetailsEditorViewModel Editor, SpectrogramViewModel Spectrogram);
    private readonly AppViewModel _app;
    private readonly MusicDatabaseService _database;
    private readonly EditorSessions _sessions;
    private readonly ILogger _logger;
    private readonly IMusicDetailsEditorFactory? _factory;
    private readonly MusicVersionDialogs? _dialogs;
    private readonly Dictionary<int, VersionSession> _versionSessions = [];
    private readonly IAsyncRelayCommand<PlaylistTarget> _add;
    private IDisposable? _session;
    private Task _playlistTask = Task.CompletedTask, _versionsTask = Task.CompletedTask, _mutationTask = Task.CompletedTask;
    private Task? _stopTask;
    private bool _started, _closing, _reloadRequested;
    private MusicVersionItem? _selectedVersion;
    private int _inspectedMissingVersionId;
    private MusicDetailsRequest? _playbackRequest;
    /// <summary>获取当前版本编辑器，其他版本草稿仍由会话保留。</summary>
    public MusicDetailsEditorViewModel Editor { get; private set => SetProperty(ref field, value); }
    /// <summary>获取当前版本频谱。</summary>
    public SpectrogramViewModel Spectrogram { get; private set => SetProperty(ref field, value); }
    /// <summary>获取当前查看文件。</summary>
    public Music Music => Editor.Music;
    /// <summary>获取推荐原因。</summary>
    public string Reason { get; }
    /// <summary>获取播放当前版本命令。</summary>
    public ICommand PlayCommand { get; }
    /// <summary>获取包含原出现项上下文的精确版本请求。</summary>
    public object? PlayParameter { get; private set => SetProperty(ref field, value); }
    /// <summary>获取可查看版本。</summary>
    public ObservableCollection<MusicVersionItem> Versions { get; } = [];
    /// <summary>获取或设置查看版本，不改变实际播放和首选。</summary>
    public MusicVersionItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (value is null || _closing || value == _selectedVersion) return;
            _inspectedMissingVersionId = value.Music.IsMissing ? value.Music.Id : 0;
            ShowVersion(value);
        }
    }
    /// <summary>获取是否正在执行版本操作。</summary>
    public bool IsVersionBusy { get; private set { if (SetProperty(ref field, value)) NotifyVersionCommands(); } }
    /// <summary>获取选版模式说明。</summary>
    public string VersionSummary { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>获取版本操作反馈。</summary>
    public string VersionMessage { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>解释有草稿时暂不能合并、拆分或撤销。</summary>
    public string VersionDraftHint
    {
        get
        {
            foreach (var session in _versionSessions.Values)
                if (session.Editor.HasChanges) return ToolUtils.GetString("SongVersionsDraftHint");
            return "";
        }
    }
    /// <summary>获取手动首选命令。</summary>
    public IAsyncRelayCommand SetPreferredCommand { get; }
    /// <summary>获取自动选版命令。</summary>
    public IAsyncRelayCommand UseAutomaticCommand { get; }
    /// <summary>获取关联候选命令。</summary>
    public IAsyncRelayCommand LinkVersionCommand { get; }
    /// <summary>获取拆出命令。</summary>
    public IAsyncRelayCommand SplitVersionCommand { get; }
    /// <summary>获取移除当前缺失版本的命令。</summary>
    public IAsyncRelayCommand RemoveMissingVersionCommand { get; }
    /// <summary>获取有条件撤销命令。</summary>
    public IAsyncRelayCommand UndoVersionCommand { get; }
    /// <summary>获取恢复共享歌词命令。</summary>
    public IAsyncRelayCommand UseSharedLyricsCommand { get; }
    /// <summary>获取歌单菜单。</summary>
    public List<MenuModel> PlaylistMenuItems { get; } = [];
    /// <summary>获取菜单准备命令。</summary>
    public IRelayCommand PreparePlaylistMenuCommand { get; }
    /// <summary>获取歌单命令错误。</summary>
    public string ErrorMessage { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>在当前版本已接受后台保存、文件保存成功或歌曲已不存在时请求宿主结束详情。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>初始化详情，Start 后才读取文件及注册退出屏障。</summary>
    public MusicDetailsViewModel(MusicDetailsEditorViewModel editor, AppViewModel app,
        MusicDatabaseService database, EditorSessions sessions, ILogger logger,
        ICommand playCommand, object? playParameter, SpectrogramViewModel spectrogram, string reason = "",
        IMusicDetailsEditorFactory? factory = null, MusicVersionDialogs? dialogs = null)
    {
        Editor = editor;
        Spectrogram = spectrogram;
        _app = app;
        _database = database;
        _sessions = sessions;
        _logger = logger;
        _factory = factory;
        _dialogs = dialogs;
        PlayCommand = playCommand;
        PlayParameter = playParameter;
        _playbackRequest = playParameter as MusicDetailsRequest;
        Reason = reason;
        _versionSessions.Add(editor.Music.Id, new(editor, spectrogram));
        PreparePlaylistMenuCommand = new RelayCommand(PrepareMenu);
        _add = new AsyncRelayCommand<PlaylistTarget>(EnqueueAdd, CanAdd, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        SetPreferredCommand = new AsyncRelayCommand(() => SetPreferenceAsync(automatic: false), CanManage);
        UseAutomaticCommand = new AsyncRelayCommand(() => SetPreferenceAsync(automatic: true), CanManage);
        LinkVersionCommand = new AsyncRelayCommand(() => MutateAsync(LinkAsync), () => CanRestructure() && _dialogs is not null);
        SplitVersionCommand = new AsyncRelayCommand(() => MutateAsync(SplitAsync), () => CanRestructure() && Versions.Count > 1);
        RemoveMissingVersionCommand = new AsyncRelayCommand(RemoveMissingAsync, CanRemoveMissing);
        UndoVersionCommand = new AsyncRelayCommand(() => MutateAsync(UndoAsync), CanRestructure);
        UseSharedLyricsCommand = new AsyncRelayCommand(() => MutateAsync(() => _database.Versions.UseSharedLyricsAsync(Music.Id)),
            () => CanManage() && Music.HasVersionLyrics && !Editor.Lyrics.HasChanges);
    }

    /// <summary>开始当前编辑器和版本列表读取。</summary>
    public void Start()
    {
        if (_started || _closing) return;
        _started = true;
        _session = _sessions.Attach(StopAsync);
        Editor.Saved += EditorSaved;
        ObserveEditor(Editor);
        _app.SongsSourceChanged += LibraryChanged;
        _app.SharedFavoriteChanged += SharedFavoriteChanged;
        _app.PropertyChanged += AppChanged;
        Editor.Start();
        LibraryChanged();
    }
    private bool CanManage() => !_closing && !IsVersionBusy && Music.Id > 0 && _database.Versions is not null;
    private bool CanRestructure()
    {
        if (!CanManage()) return false;
        foreach (var session in _versionSessions.Values)
            if (session.Editor.HasChanges || session.Editor.IsBusy) return false;
        return true;
    }
    private void ObserveEditor(MusicDetailsEditorViewModel editor)
    {
        editor.CloseRequested += EditorCloseRequested;
        editor.PropertyChanged += DraftChanged;
        editor.Metadata.Draft.PropertyChanged += DraftChanged;
        editor.Lyrics.PropertyChanged += DraftChanged;
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs args) => NotifyVersionCommands();
    private void NotifyVersionCommands()
    {
        OnPropertyChanged(nameof(VersionDraftHint));
        SetPreferredCommand.NotifyCanExecuteChanged();
        UseAutomaticCommand.NotifyCanExecuteChanged();
        LinkVersionCommand.NotifyCanExecuteChanged();
        SplitVersionCommand.NotifyCanExecuteChanged();
        RemoveMissingVersionCommand.NotifyCanExecuteChanged();
        UndoVersionCommand.NotifyCanExecuteChanged();
        UseSharedLyricsCommand.NotifyCanExecuteChanged();
    }
    private void ShowVersion(MusicVersionItem row)
    {
        if (!SetProperty(ref _selectedVersion, row, nameof(SelectedVersion))) return;
        Music music = row.Music;
        if (!_versionSessions.TryGetValue(music.Id, out var session))
        {
            if (_factory is null) return;
            var editor = _factory.Create(music);
            editor.Lyrics.ShareDraftWith(Editor.Lyrics);
            session = new(editor, _factory.CreateSpectrogram(music));
            _versionSessions.Add(music.Id, session);
            editor.Saved += EditorSaved;
            ObserveEditor(editor);
        }
        if (!ReferenceEquals(Editor, session.Editor))
        {
            Spectrogram.SetActive(false);
            session.Editor.Lyrics.ShareDraftWith(Editor.Lyrics);
            Editor = session.Editor;
            Spectrogram = session.Spectrogram;
            OnPropertyChanged(nameof(Music));
            session.Editor.Start();
        }
        if (_playbackRequest is not null) PlayParameter = _playbackRequest with { Music = Music, ExactVersion = true };
        NotifyVersionCommands();
    }
    private async Task ReloadVersionsAsync(bool chooseAutomatic = false)
    {
        try
        {
            int selected = Music.Id;
            var versions = selected > 0 && _database.Versions is not null
                ? await _database.Versions.GetVersionsAsync(selected) : new List<Music> { Music };
            if (_closing) return;
            selected = Music.Id;
            Versions.Clear();
            if (versions.Count == 0) { CloseRequested?.Invoke(this, EventArgs.Empty); return; }
            MusicVersionItem? choice = null;
            List<LyricsEditorViewModel> documentsToReload = [];
            foreach (var version in versions)
            {
                Music item = _app.FindById(version.Id) ?? version;
                version.IsRemoteCached = item.IsRemoteCached;
                version.IsRemoteOffline = item.IsRemoteOffline;
                if (_versionSessions.TryGetValue(version.Id, out var existing))
                {
                    existing.Editor.Metadata.ApplyReloaded(version);
                    item = existing.Editor.Music;
                    int expectedOwner = version.HasVersionLyrics ? -version.Id : version.SongId;
                    if (existing.Editor.Lyrics.DocumentOwnerId != 0 &&
                        (existing.Editor.Lyrics.DocumentOwnerId != expectedOwner || !existing.Editor.Lyrics.HasChanges))
                        documentsToReload.Add(existing.Editor.Lyrics);
                    item.SongId = version.SongId;
                    item.SongPreferredMusicId = version.SongPreferredMusicId;
                    item.HasVersionLyrics = version.HasVersionLyrics;
                    item.IsFavorite = version.IsFavorite;
                    item.IsRemoteCached = version.IsRemoteCached;
                    item.IsRemoteOffline = version.IsRemoteOffline;
                }
                item.IsMissing = version.IsMissing;
                var row = new MusicVersionItem(item);
                Versions.Add(row);
                if (version.Id == selected) choice = row;
            }
            if (choice?.Music.IsMissing != true) _inspectedMissingVersionId = 0;
            else if (_selectedVersion is null && _playbackRequest?.ExactVersion == true) _inspectedMissingVersionId = selected;
            bool followMissing = choice?.Music.IsMissing == true && versions[0].SongPreferredMusicId is null && _inspectedMissingVersionId != selected;
            if (chooseAutomatic || choice is null || followMissing)
            {
                int best = SongVersionPolicy.Order(versions, versions[0].SongPreferredMusicId)[0].Id;
                foreach (var row in Versions)
                    if (row.Music.Id == best) { choice = row; break; }
                _inspectedMissingVersionId = 0;
            }
            ShowVersion(choice!);
            foreach (var lyrics in documentsToReload)
            {
                if (_closing) return;
                await lyrics.ReloadIfUnchangedAsync();
            }
            RefreshVersionStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ReportVersionError(exception); }
    }
    private void RefreshVersionStatus()
    {
        foreach (var row in Versions) row.Refresh(Music.SongPreferredMusicId, _app.CurrentPlayingMusic?.Id ?? 0);
        VersionSummary = ToolUtils.GetString(Music.SongPreferredMusicId is null ? "SongVersionsAuto" : "SongVersionsManual");
        NotifyVersionCommands();
    }
    private Task SetPreferenceAsync(bool automatic)
    {
        int id = Music.Id;
        return MutateAsync(() => _database.Versions.SetPreferredAsync(id, automatic ? null : id), id, automatic);
    }
    private Task MutateAsync(Func<Task> action, int? versionStateId = null, bool chooseAutomatic = false)
        => !CanManage() ? Task.CompletedTask : _mutationTask = MutateCoreAsync(action, versionStateId, chooseAutomatic);
    private async Task MutateCoreAsync(Func<Task> action, int? versionStateId, bool chooseAutomatic)
    {
        IsVersionBusy = true;
        VersionMessage = "";
        try
        {
            await action();
            await _versionsTask;
            if (versionStateId is int id)
                await _database.PublishVersionStateAsync(id);
            else
                await _app.RefreshSongsSourceAsync();
            if (_closing) return;
            _reloadRequested = false;
            await ReloadVersionsAsync(chooseAutomatic);
            if (_closing || Versions.Count == 0) return;
            await Editor.Lyrics.ReloadIfUnchangedAsync();
            if (!_closing) VersionMessage = ToolUtils.GetString("SongVersionsSaved");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ReportVersionError(exception); }
        finally
        {
            IsVersionBusy = false;
            if (_reloadRequested) LibraryChanged();
        }
    }
    private async Task LinkAsync()
    {
        var candidates = await _database.Versions.GetCandidatesAsync(Music);
        if (candidates.Count == 0) throw new InvalidOperationException("SongVersionsNoCandidates");
        var selected = await _dialogs!.ChooseAsync(candidates);
        if (selected is null || _closing) throw new OperationCanceledException();
        var preview = await _database.Versions.PreviewMergeAsync(Music.Id, selected.Id, _database.Lyrics);
        var versions = await _database.Versions.GetVersionsAsync(Music.Id);
        versions.AddRange(await _database.Versions.GetVersionsAsync(selected.Id));
        var decision = await _dialogs.ConfirmAsync(preview, versions);
        if (decision is null || _closing) throw new OperationCanceledException();
        using var lease = await LibraryOperationGate.EnterAsync();
        await _database.Versions.MergeAsync(preview, decision.LyricsSongId, decision.CollapseDuplicates);
    }
    private async Task UndoAsync()
    {
        var operation = await _database.Versions.GetLastOperationAsync(Music.SongId);
        if (operation is null) throw new InvalidOperationException("SongVersionsNoOperation");
        using var lease = await LibraryOperationGate.EnterAsync();
        await _database.Versions.UndoAsync(operation.Id);
    }
    private bool CanRemoveMissing() => SelectedVersion?.Music.IsMissing == true && CanRestructure();
    private Task RemoveMissingAsync()
    {
        if (!CanRemoveMissing()) return Task.CompletedTask;
        int musicId = SelectedVersion!.Music.Id;
        return MutateAsync(async () =>
        {
            using var lease = await LibraryOperationGate.EnterAsync();
            await _database.Versions.RemoveMissingVersionAsync(musicId);
        }, musicId);
    }
    private async Task SplitAsync()
    {
        using var lease = await LibraryOperationGate.EnterAsync();
        await _database.Versions.SplitAsync(Music.Id, _database.Lyrics);
    }
    private void ReportVersionError(Exception exception)
    {
        _logger.LogError(exception, "歌曲版本操作失败");
        VersionMessage = exception.Message.StartsWith("SongVersions", StringComparison.Ordinal)
            ? ToolUtils.GetString(exception.Message) : ToolUtils.GetString("SongVersionsConflict");
    }
    private void EditorSaved(object? sender, EventArgs args)
    {
        if (_closing) return;
        VersionMessage = ToolUtils.GetString("SongVersionsSaved");
        if (_app.CurrentPlayingMusic is { } current && current.SongId == Music.SongId) _app.LoadLyricsToUI(current);
        EditorCloseRequested(sender, args);
    }
    private void EditorCloseRequested(object? sender, EventArgs args)
    {
        if (!_closing && ReferenceEquals(sender, Editor)) CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private void LibraryChanged()
    {
        if (_closing) return;
        _reloadRequested = true;
        if (!IsVersionBusy && _versionsTask.IsCompleted) _versionsTask = ReloadPendingVersionsAsync();
    }
    private async Task ReloadPendingVersionsAsync()
    {
        while (_reloadRequested && !_closing && !IsVersionBusy)
        {
            _reloadRequested = false;
            await ReloadVersionsAsync();
        }
    }
    private void SharedFavoriteChanged()
    {
        if (_closing) return;
        foreach (var session in _versionSessions.Values)
            if (_app.FindById(session.Editor.Music.Id) is { } current)
            {
                session.Editor.Music.IsFavorite = current.IsFavorite;
                session.Editor.Music.Order = current.Order;
            }
    }
    private void AppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closing) return;
        if (args.PropertyName is nameof(AppViewModel.IsPlaybackEngineReady) or nameof(AppViewModel.IsInitialized))
            (PlayCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        if (args.PropertyName == nameof(AppViewModel.CurrentPlayingMusic)) RefreshVersionStatus();
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
        => !CanAdd(target) ? Task.CompletedTask : _playlistTask = AddAfterAsync(_playlistTask, target!);
    private async Task AddAfterAsync(Task previous, PlaylistTarget target)
    {
        await previous;
        if (_app.FindById(target.MusicId) is null || !PlaylistExists(target.PlaylistId)) return;
        try { await _database.AddMusicToPlayList(target.PlaylistId, target.MusicId); if (!_closing) ErrorMessage = ""; }
        catch (Exception exception) { _logger.LogError(exception, "添加歌曲到歌单失败"); if (!_closing) ErrorMessage = ToolUtils.GetString("HomePlaylistAddFailed"); }
    }
    /// <summary>更新入口上下文，并在相同歌曲内切换查看版本。</summary>
    public void SetPlaybackParameter(MusicDetailsRequest request)
    {
        if (_closing) return;
        _playbackRequest = request;
        foreach (var row in Versions) if (row.Music.Id == request.Music.Id) { SelectedVersion = row; break; }
        PlayParameter = request with { Music = Music, ExactVersion = true };
    }
    /// <summary>停止所有版本订阅，已接受写入仍由 StopAsync 等待。</summary>
    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _dialogs?.Cancel();
        _app.SongsSourceChanged -= LibraryChanged;
        _app.SharedFavoriteChanged -= SharedFavoriteChanged;
        _app.PropertyChanged -= AppChanged;
        foreach (var session in _versionSessions.Values)
        {
            session.Editor.Saved -= EditorSaved;
            session.Editor.CloseRequested -= EditorCloseRequested;
            session.Editor.PropertyChanged -= DraftChanged;
            session.Editor.Metadata.Draft.PropertyChanged -= DraftChanged;
            session.Editor.Lyrics.PropertyChanged -= DraftChanged;
            session.Editor.BeginClose();
            session.Spectrogram.BeginClose();
        }
        CloseRequested = null;
        PlaylistMenuItems.Clear();
        _add.NotifyCanExecuteChanged();
        NotifyVersionCommands();
    }
    /// <summary>等待全部在途任务后释放退出屏障。</summary>
    public Task StopAsync() => _stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        BeginClose();
        List<Task> pending = [_playlistTask, _versionsTask, _mutationTask];
        foreach (var session in _versionSessions.Values)
        {
            pending.Add(session.Editor.StopAsync());
            pending.Add(session.Spectrogram.StopAsync());
        }
        try { await Task.WhenAll(pending); }
        catch (Exception exception) { _logger.LogError(exception, "结束歌曲详情会话失败"); }
        finally
        {
            PlayParameter = null;
            Versions.Clear();
            _versionSessions.Clear();
            PlaylistMenuItems.Clear();
            _session?.Dispose();
            _session = null;
        }
    }
}
