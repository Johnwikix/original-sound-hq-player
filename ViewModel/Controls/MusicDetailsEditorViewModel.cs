using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>复用音乐属性窗口的标签草稿、歌词版本和持久化入口，拥有一次页内编辑会话。</summary>
public sealed class MusicDetailsEditorViewModel : ObservableObject
{
    private readonly MusicDatabaseService _database;
    private readonly EditorSessions _sessions;
    private readonly IMusicDetailsEditorHost _host;
    private readonly ILogger<MusicDetailsEditorViewModel> _logger;
    private readonly CancellationTokenSource _closed = new();
    private IDisposable? _session;
    private byte[]? _cover;
    private Task? _stopTask;
    private Task _operationTask = Task.CompletedTask;
    private bool _stopped;
    private bool _coverDirty;
    /// <summary>获取规范曲目，仅成功提交后发布标签。</summary>
    public Music Music { get; }
    /// <summary>获取与 MusicDetailsWindow 相同的标签草稿。</summary>
    public MusicMetadataEditorViewModel Metadata { get; }
    /// <summary>获取标签、封面或歌词是否有未保存输入。</summary>
    public bool HasChanges => Metadata.HasChanges || Lyrics.HasChanges || _coverDirty;
    /// <summary>获取与 MusicDetailsWindow 相同的原文、翻译、注音和恢复命令。</summary>
    public LyricsEditorViewModel Lyrics { get; }
    /// <summary>获取暂存封面；不直接改写正在播放的图片。</summary>
    public BitmapImage? CoverPreview { get; private set => SetProperty(ref field, value); }
    /// <summary>获取初始化和重试是否正在读取。</summary>
    public bool IsLoading { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取是否正在提交快照。</summary>
    public bool IsSaving { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取是否正在选择或读取封面。</summary>
    public bool IsCoverBusy { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取加载、保存、封面或歌词操作是否正在进行。</summary>
    public bool IsBusy => IsLoading || IsSaving || IsCoverBusy || Lyrics.IsBusy;
    /// <summary>获取加载、封面或保存错误。</summary>
    public string Error { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>获取是否需要重试初始化读取，不把封面或保存错误误当作重新加载草稿。</summary>
    public bool LoadFailed { get; private set => SetProperty(ref field, value); }
    /// <summary>获取标签是否可编辑；远程曲目沿用只读文件标签策略。</summary>
    public bool CanEditTags => !_stopped && !IsLoading && Music.CanEditFile;
    private bool CanSave => !_stopped && !IsLoading && !IsSaving && !IsCoverBusy && Lyrics.CanSave;
    /// <summary>获取是否可以确认写入文件；远程来源只允许保存本地数据库歌词。</summary>
    public bool CanSaveFile => CanSave && Music.CanEditFile;
    private bool CanOperate => !_stopped && !IsLoading && !IsSaving && !IsCoverBusy && !Lyrics.IsBusy;
    /// <summary>获取加载或失败重试命令。</summary>
    public IAsyncRelayCommand LoadCommand { get; }
    /// <summary>获取仅保存到数据库的命令。</summary>
    public IAsyncRelayCommand SaveDatabaseCommand { get; }
    /// <summary>获取与属性窗口相同的文件写入队列命令。</summary>
    public IAsyncRelayCommand SaveFileCommand { get; }
    /// <summary>获取网络封面命令。</summary>
    public IAsyncRelayCommand FindCoverCommand { get; }
    /// <summary>获取本地替换封面命令。</summary>
    public IAsyncRelayCommand ChooseCoverCommand { get; }
    /// <summary>获取导出封面命令。</summary>
    public IAsyncRelayCommand ExportCoverCommand { get; }
    /// <summary>获取读取文件歌词的命令。</summary>
    public IAsyncRelayCommand ReadEmbeddedCommand { get; }
    /// <summary>获取打开文件位置的命令。</summary>
    public IRelayCommand OpenLocationCommand { get; }
    /// <summary>数据库保存已被应用级后台队列接收，宿主现在可以关闭详情。</summary>
    public event EventHandler? CloseRequested;
    /// <summary>文件标签写入已成功排队并保存完整当前草稿。</summary>
    public event EventHandler? Saved;

    /// <summary>初始化独立草稿和命令；显式 Start 后才读取数据。</summary>
    public MusicDetailsEditorViewModel(Music music, MusicDatabaseService database, LyricsEditorViewModel lyrics,
        EditorSessions sessions, IMusicDetailsEditorHost host, ILogger<MusicDetailsEditorViewModel> logger)
    {
        Music = music;
        Metadata = new(music);
        Lyrics = lyrics;
        _database = database;
        _sessions = sessions;
        _host = host;
        _logger = logger;
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => CanOperate);
        SaveDatabaseCommand = new AsyncRelayCommand(SaveDatabaseAsync, () => CanSave);
        SaveFileCommand = new AsyncRelayCommand(SaveFileAsync, () => CanSave && Music.CanEditFile);
        FindCoverCommand = new AsyncRelayCommand(() => ChangeCoverAsync(true), () => CanOperate);
        ChooseCoverCommand = new AsyncRelayCommand(() => ChangeCoverAsync(false), () => CanOperate && Music.CanEditFile);
        ExportCoverCommand = new AsyncRelayCommand(ExportCoverAsync, () => CanOperate && _cover is not null);
        ReadEmbeddedCommand = new AsyncRelayCommand(ReadEmbeddedAsync, () => CanOperate && Music.CanEditFile);
        OpenLocationCommand = new RelayCommand(OpenLocation, () => !_stopped && Music.CanEditFile);
        Lyrics.PropertyChanged += Lyrics_PropertyChanged;
    }
    /// <summary>注册退出屏障并开始异步加载，不阻塞 Connected Animation。</summary>
    public void Start()
    {
        if (_stopped || _session is not null) return;
        _session = _sessions.Attach(StopAsync);
        _ = LoadCommand.ExecuteAsync(null); // LoadAsync 自行观察错误，实际任务由 StopAsync 等待。
    }
    private void Lyrics_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LyricsEditorViewModel.IsBusy) or nameof(LyricsEditorViewModel.CanSave)) NotifyCommands();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEditTags));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSaveFile));
        LoadCommand.NotifyCanExecuteChanged();
        SaveDatabaseCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
        FindCoverCommand.NotifyCanExecuteChanged();
        ChooseCoverCommand.NotifyCanExecuteChanged();
        ExportCoverCommand.NotifyCanExecuteChanged();
        ReadEmbeddedCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
    }
    private Task LoadAsync() => CanOperate ? _operationTask = LoadCoreAsync() : Task.CompletedTask;
    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        Error = "";
        LoadFailed = false;
        try
        {
            await Lyrics.LoadAsync();
            _closed.Token.ThrowIfCancellationRequested();
            if (!Lyrics.CanSave)
            {
                LoadFailed = true;
                Error = Lyrics.Error.Length > 0 ? Lyrics.Error : ToolUtils.GetString("Error");
                return;
            }
            var tags = await _database.ReadAdditionalMetadataAsync(Music, _closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            if (tags is { } loaded && Metadata.ApplyLoaded(loaded)) _host.MetadataChanged();
            var bytes = await _host.ReadCoverAsync(Music, _closed.Token);
            // 初始无图是正常状态；主动获取或选择空文件仍由 ApplyCoverAsync 报错。
            if (bytes is { Length: > 0 }) await ApplyCoverAsync(bytes);
        }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex) { if (!_stopped) LoadFailed = true; ReportError(ex); }
        finally { IsLoading = false; }
    }
    private async Task ApplyCoverAsync(byte[]? bytes)
    {
        if (bytes is null || _stopped) return;
        var preview = await ToolUtils.ConvertByteArrayToBitmapImage(bytes, 600);
        _closed.Token.ThrowIfCancellationRequested();
        if (preview is null) throw new InvalidOperationException(ToolUtils.GetString("FailedObtainCover"));
        _cover = bytes;
        CoverPreview = preview;
        ExportCoverCommand.NotifyCanExecuteChanged();
    }
    private Task ChangeCoverAsync(bool online)
        => CanOperate && (online || Music.CanEditFile) ? _operationTask = ChangeCoverCoreAsync(online) : Task.CompletedTask;
    private async Task ChangeCoverCoreAsync(bool online)
    {
        IsCoverBusy = true;
        Error = "";
        LoadFailed = false;
        try
        {
            var bytes = online ? await _host.FindCoverAsync(Music, _closed.Token) : await _host.ChooseCoverAsync(_closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            if (online && bytes is null) throw new InvalidOperationException(ToolUtils.GetString("FailedObtainCover"));
            await ApplyCoverAsync(bytes);
            if (bytes is not null)
            {
                _coverDirty = true;
                OnPropertyChanged(nameof(HasChanges));
            }
        }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex) { ReportError(ex); }
        finally { IsCoverBusy = false; }
    }
    private Task ExportCoverAsync()
        => CanOperate && _cover is { } cover ? _operationTask = ExportCoverCoreAsync(cover) : Task.CompletedTask;
    private async Task ExportCoverCoreAsync(byte[] cover)
    {
        IsCoverBusy = true;
        Error = "";
        LoadFailed = false;
        try { await _host.ExportCoverAsync(cover, _closed.Token); }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex) { ReportError(ex); }
        finally { IsCoverBusy = false; }
    }
    private Task SaveDatabaseAsync()
    {
        if (!CanSave) return Task.CompletedTask;
        var snapshot = Metadata.Capture();
        bool metadataChanged = Metadata.HasChanges || _coverDirty;
        IsSaving = true;
        Error = "";
        LoadFailed = false;
        if (!Lyrics.TrySaveDatabaseInBackground(snapshot, metadataChanged, _coverDirty ? _cover : null,
            () => DatabaseSaveCompleted(snapshot, metadataChanged),
            exception => DatabaseSaveFailed(snapshot, exception), out var operation))
        {
            IsSaving = false;
            return Task.CompletedTask;
        }
        // 必须先登记任务，再同步通知宿主；关窗回调可能立刻调用 StopAsync。
        _operationTask = operation;
        BeginClose();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return operation;
    }
    private void DatabaseSaveCompleted(Music snapshot, bool metadataChanged)
    {
        try
        {
            if (metadataChanged) Metadata.AcceptSaved(snapshot);
            _coverDirty = false;
            _host.DatabaseSaved(snapshot);
        }
        catch (Exception exception) { _logger.LogError(exception, "歌曲属性已保存，但刷新界面状态失败"); }
        finally { IsSaving = false; }
    }
    private void DatabaseSaveFailed(Music snapshot, Exception exception)
    {
        _logger.LogError(exception, "后台保存歌曲属性失败: {MusicId}", snapshot.Id);
        try { _host.NotifyDatabaseSaveFailed(snapshot, exception); }
        catch (Exception notificationError) { _logger.LogWarning(notificationError, "歌曲属性保存失败提示未能送达"); }
        finally { IsSaving = false; }
    }
    private Task SaveFileAsync()
        => CanSave && Music.CanEditFile ? _operationTask = SaveFileCoreAsync() : Task.CompletedTask;
    private async Task SaveFileCoreAsync()
    {
        IsSaving = true;
        Error = "";
        LoadFailed = false;
        try
        {
            var snapshot = Metadata.Capture();
            snapshot.UpdateTime = DateTime.Now;
            bool saved = await Lyrics.SaveAsync(_coverDirty ? _cover : null, queueMetadata: true, metadataDraft: snapshot);
            bool committed = ReferenceEquals(Lyrics.LastCommittedMetadata, snapshot);
            bool unchanged = committed && Metadata.AcceptSaved(snapshot);
            if (committed)
            {
                _coverDirty = false;
                OnPropertyChanged(nameof(HasChanges));
            }
            // 即使保存期间离开页面，已经提交的数据库事实仍须发布到规范曲目。
            if (committed) _host.MetadataChanged();
            if (!_stopped && saved && unchanged)
            {
                _host.NotifyWriteQueued(snapshot);
                Saved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex) { ReportError(ex); }
        finally { IsSaving = false; }
    }
    private Task ReadEmbeddedAsync() => CanOperate && Music.CanEditFile ? _operationTask = Lyrics.ReadEmbeddedAsync() : Task.CompletedTask;
    private void OpenLocation()
    {
        if (_stopped || !Music.CanEditFile) return;
        try { _host.OpenLocation(Music); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void ReportError(Exception ex)
    {
        _logger.LogError(ex, "加载或保存歌曲属性失败");
        if (!_stopped) Error = ex.Message;
    }
    /// <summary>离开属性时立即停止编辑活动，保留封面供返回动画使用。</summary>
    public void BeginClose()
    {
        if (_stopped) return;
        _stopped = true;
        _closed.Cancel();
        Lyrics.Dispose();
        Lyrics.PropertyChanged -= Lyrics_PropertyChanged;
        NotifyCommands();
    }
    /// <summary>取消可取消读取并等待真实在途任务，防止迟到结果覆盖新曲目。</summary>
    public Task StopAsync() => _stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        BeginClose();
        try
        {
            await Task.WhenAll(_operationTask, Lyrics.WhenIdleAsync());
        }
        finally
        {
            _session?.Dispose();
            _session = null;
            _cover = null;
            CoverPreview = null;
            _closed.Dispose();
        }
    }
}
