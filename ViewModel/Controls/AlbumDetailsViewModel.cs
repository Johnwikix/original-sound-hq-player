using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>专辑窗口只读展示行；标题、艺术家与时长在构建时格式化，列表不再绑定转换器。</summary>
public sealed record AlbumTrackItem(string Number, string Title, string Artist, string Duration);

/// <summary>拥有一次专辑级编辑会话：草稿只在保存成功后发布到全部曲目，封面与持久化复用属性窗口边界。</summary>
public sealed class AlbumDetailsViewModel : ObservableObject
{
    private static readonly string NumberOfSongsText = ToolUtils.GetString("NumberOfSongs");
    private static readonly string NumberOfArtistsText = ToolUtils.GetString("NumberOfArtists");

    private readonly MusicDatabaseService _database;
    private readonly IMusicDetailsEditorHost _host;
    private readonly AppViewModel _app;
    private readonly ILogger _logger;
    private readonly EditorSessions _sessions;
    private readonly CancellationTokenSource _closed = new();
    private readonly List<Music> _tracks;
    private readonly string _initialAlbum, _initialAlbumArtist, _initialGenre;
    private readonly int _initialYear;
    private IDisposable? _session;
    private byte[]? _cover;
    private byte[]? _initialCover;
    private Task _operationTask = Task.CompletedTask;
    private Task? _stopTask;
    private bool _stopped;

    /// <summary>获取代表曲目，封面与懒读可选标签以其文件为准。</summary>
    public Music Album { get; }
    /// <summary>获取仅供窗口编辑的专辑级草稿；取消或关闭不污染共享曲目。</summary>
    public Music Draft { get; }
    /// <summary>获取按碟序、曲序排序的只读曲目行。</summary>
    public IReadOnlyList<AlbumTrackItem> Tracks { get; }
    /// <summary>获取曲目艺术家的原始去重列表，以 " · " 连接，与专辑页副标题一致。</summary>
    public string TrackArtists { get; }
    /// <summary>获取"年份 · 曲目数[ · 艺术家数] · 总时长"概要行。</summary>
    public string Summary { get; }
    /// <summary>获取封面预览；不直接改写正在播放的图片。</summary>
    public BitmapImage? CoverPreview { get; private set => SetProperty(ref field, value); }
    /// <summary>获取初始化和重试是否正在读取。</summary>
    public bool IsLoading { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取是否正在提交变更。</summary>
    public bool IsSaving { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取是否正在选择或读取封面。</summary>
    public bool IsCoverBusy { get; private set { if (SetProperty(ref field, value)) NotifyCommands(); } }
    /// <summary>获取加载、封面或保存操作是否正在进行。</summary>
    public bool IsBusy => IsLoading || IsSaving || IsCoverBusy;
    /// <summary>获取加载、封面或保存错误。</summary>
    public string Error { get; private set => SetProperty(ref field, value); } = "";
    /// <summary>获取是否需要重试初始化读取。</summary>
    public bool LoadFailed { get; private set => SetProperty(ref field, value); }
    /// <summary>获取标签是否可编辑；远程专辑沿用只读策略。</summary>
    public bool CanEditTags => !_stopped && !IsLoading && Album.CanEditFile;
    private bool CanSave => !_stopped && !IsLoading && !IsSaving && !IsCoverBusy;
    /// <summary>获取是否可以写入文件；远程来源没有文件通道。</summary>
    public bool CanSaveFile => CanSave && Album.CanEditFile;
    /// <summary>获取是否可以仅保存数据库；远程专辑没有可用的落盘通道，两个保存入口一并禁用。</summary>
    public bool CanSaveDatabase => CanSave && Album.CanEditFile;
    private bool CanOperate => !_stopped && !IsLoading && !IsSaving && !IsCoverBusy;
    /// <summary>获取加载或失败重试命令。</summary>
    public IAsyncRelayCommand LoadCommand { get; }
    /// <summary>获取写入全部曲目文件的命令。</summary>
    public IAsyncRelayCommand SaveFileCommand { get; }
    /// <summary>获取仅保存数据库的命令。</summary>
    public IAsyncRelayCommand SaveDatabaseCommand { get; }
    /// <summary>获取网络封面命令；专辑窗口没有远程可用的落盘通道，与文件保存同门槛。</summary>
    public IAsyncRelayCommand FindCoverCommand { get; }
    /// <summary>获取本地替换封面命令。</summary>
    public IAsyncRelayCommand ChooseCoverCommand { get; }
    /// <summary>获取导出封面命令。</summary>
    public IAsyncRelayCommand ExportCoverCommand { get; }
    /// <summary>保存成功或代表曲目被库替换时请求宿主关闭窗口。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>初始化专辑会话；显式 Start 后才读取数据。</summary>
    public AlbumDetailsViewModel(Music album, AppViewModel app, MusicDatabaseService database,
        EditorSessions sessions, IMusicDetailsEditorHost host, ILogger<AlbumDetailsViewModel> logger)
    {
        Album = album;
        _app = app;
        _database = database;
        _sessions = sessions;
        _host = host;
        _logger = logger;
        Draft = new Music
        {
            Album = album.Album, Year = album.Year,
            AlbumArtist = album.AlbumArtist, Genre = album.Genre,
            AdditionalMetadataRead = album.AdditionalMetadataRead
        };
        _initialAlbum = Draft.Album;
        _initialYear = Draft.Year;
        _initialAlbumArtist = Draft.AlbumArtist;
        _initialGenre = Draft.Genre;
        _tracks = [];
        foreach (var music in app.SongsSource)
            if (music.Album == album.Album && music.SourceId == album.SourceId) _tracks.Add(music);
        _tracks.Sort((a, b) => a.DiskNumber != b.DiskNumber ? a.DiskNumber.CompareTo(b.DiskNumber)
            : a.TrackNumber != b.TrackNumber ? a.TrackNumber.CompareTo(b.TrackNumber) : string.CompareOrdinal(a.Title, b.Title));
        Tracks = BuildTrackItems(_tracks);
        (TrackArtists, int artistCount, TimeSpan total) = SummarizeTracks(_tracks);
        Summary = BuildSummary(album.Year, _tracks.Count, artistCount, total);
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => CanOperate);
        SaveFileCommand = new AsyncRelayCommand(() => SaveAsync(true), () => CanSaveFile);
        SaveDatabaseCommand = new AsyncRelayCommand(() => SaveAsync(false), () => CanSaveDatabase);
        FindCoverCommand = new AsyncRelayCommand(() => ChangeCoverAsync(true), () => CanOperate && Album.CanEditFile);
        ChooseCoverCommand = new AsyncRelayCommand(() => ChangeCoverAsync(false), () => CanOperate && Album.CanEditFile);
        ExportCoverCommand = new AsyncRelayCommand(ExportCoverAsync, () => CanOperate && _cover is not null);
        Draft.PropertyChanged += DraftChanged;
    }

    /// <summary>获取首行艺术家展示：专辑艺术家优先，缺省回退曲目艺术家列表。</summary>
    public string HeroArtist => string.IsNullOrWhiteSpace(Draft.AlbumArtist) ? TrackArtists : Draft.AlbumArtist;

    private void DraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Music.AlbumArtist)) OnPropertyChanged(nameof(HeroArtist));
    }

    /// <summary>注册退出屏障并开始异步加载，不阻塞窗口显示。</summary>
    public void Start()
    {
        if (_stopped || _session is not null) return;
        _session = _sessions.Attach(StopAsync);
        _app.SongsSourceChanged += LibraryChanged;
        _ = LoadCommand.ExecuteAsync(null); // LoadAsync 自行观察错误，实际任务由 StopAsync 等待。
    }

    private void LibraryChanged()
    {
        // 保存触发的刷新只重建投影、不换对象；真正被重扫替换的代表曲目应关闭编辑。
        if (!_stopped && Album.Id != 0 && !ReferenceEquals(_app.FindById(Album.Id), Album))
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEditTags));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanSaveFile));
        OnPropertyChanged(nameof(CanSaveDatabase));
        LoadCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
        SaveDatabaseCommand.NotifyCanExecuteChanged();
        FindCoverCommand.NotifyCanExecuteChanged();
        ChooseCoverCommand.NotifyCanExecuteChanged();
        ExportCoverCommand.NotifyCanExecuteChanged();
    }

    private Task LoadAsync() => CanOperate ? _operationTask = LoadCoreAsync() : Task.CompletedTask;
    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        Error = "";
        LoadFailed = false;
        try
        {
            var tags = await _database.ReadAdditionalMetadataAsync(Album, _closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            if (tags is { } loaded)
            {
                bool changed = AdditionalMusicMetadata.From(Album) != loaded;
                loaded.ApplyTo(Album);
                // 草稿未被用户改动时同步补读结果，并重置初始快照，避免把补读误判为修改。
                if (Draft.AlbumArtist == _initialAlbumArtist && Draft.Genre == _initialGenre)
                {
                    Draft.AlbumArtist = loaded.AlbumArtist;
                    Draft.Genre = loaded.Genre;
                    Draft.AdditionalMetadataRead = true;
                }
                if (changed) _host.MetadataChanged();
            }
            var bytes = await _host.ReadCoverAsync(Album, _closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            // 初始无图是正常状态；仅记录原始字节供保存时判断封面是否被更换。
            if (bytes is { Length: > 0 }) await ApplyCoverAsync(bytes);
            _initialCover = _cover;
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
        => CanOperate && Album.CanEditFile ? _operationTask = ChangeCoverCoreAsync(online) : Task.CompletedTask;
    private async Task ChangeCoverCoreAsync(bool online)
    {
        IsCoverBusy = true;
        Error = "";
        LoadFailed = false;
        try
        {
            var bytes = online ? await _host.FindCoverAsync(Album, _closed.Token) : await _host.ChooseCoverAsync(_closed.Token);
            _closed.Token.ThrowIfCancellationRequested();
            if (online && bytes is null) throw new InvalidOperationException(ToolUtils.GetString("FailedObtainCover"));
            await ApplyCoverAsync(bytes);
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

    private Task SaveAsync(bool file)
    {
        if (!file ? !CanSaveDatabase : !CanSaveFile) return Task.CompletedTask;
        return _operationTask = SaveCoreAsync(file);
    }
    private async Task SaveCoreAsync(bool file)
    {
        IsSaving = true;
        Error = "";
        LoadFailed = false;
        try
        {
            var edit = BuildEdit();
            if (edit.IsEmpty)
            {
                // 没有改动视作保存成功；不产生任何 I/O。
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (file)
            {
                // 写入批次一旦开始就跑完：中途取消会留下半更新的专辑（部分曲目已写文件+入库）。
                // 关窗/关应用由 EditorSessions 退出屏障等待 _operationTask 真实结束。
                var failed = await _database.WriteAlbumTagsAsync(_tracks, edit, CancellationToken.None);
                if (failed.Count > 0)
                {
                    // 部分曲目被占用或失败时保留窗口与错误信息，其余已提交的事实不回滚。
                    if (!_stopped) Error = string.Format(ToolUtils.GetString("AlbumDetailsWriteFailed"), failed.Count, failed[0]);
                    return;
                }
            }
            else
            {
                await _database.SaveAlbumTagsAsync(_tracks, edit, _closed.Token);
                _closed.Token.ThrowIfCancellationRequested();
            }
            // 即使保存期间窗口已关闭，已提交的库内事实仍须发布刷新。
            _host.MetadataChanged();
            if (!_stopped) CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_stopped) { }
        catch (Exception ex) { ReportError(ex); }
        finally { IsSaving = false; }
    }

    /// <summary>与初始快照对比生成变更集；未被用户改动的字段不进入写入，避免无谓重写或清空未加载的可选标签。</summary>
    private AlbumTagEdit BuildEdit() => new(
        Album: Draft.Album != _initialAlbum ? Draft.Album.Trim() : null,
        Year: Draft.Year != _initialYear ? Draft.Year : null,
        AlbumArtist: Draft.AlbumArtist != _initialAlbumArtist ? Draft.AlbumArtist.Trim() : null,
        Genre: Draft.Genre != _initialGenre ? Draft.Genre.Trim() : null,
        Cover: !ReferenceEquals(_cover, _initialCover) ? _cover : null);

    private void ReportError(Exception ex)
    {
        _logger.LogError(ex, "加载或保存专辑属性失败");
        if (!_stopped) Error = ex.Message;
    }

    /// <summary>离开窗口立即停止编辑活动；封面预览保留给关闭动画可能的后续读取。</summary>
    public void BeginClose()
    {
        if (_stopped) return;
        _stopped = true;
        _closed.Cancel();
        Draft.PropertyChanged -= DraftChanged;
        _app.SongsSourceChanged -= LibraryChanged;
        CloseRequested = null;
        NotifyCommands();
    }

    /// <summary>取消可取消读取并等待真实在途任务，防止迟到结果覆盖新会话。</summary>
    public Task StopAsync() => _stopTask ??= StopCoreAsync();
    private async Task StopCoreAsync()
    {
        BeginClose();
        _session?.Dispose();
        _session = null;
        try { await _operationTask; }
        catch (Exception exception) { _logger.LogError(exception, "结束专辑属性会话失败"); }
        finally
        {
            _cover = null;
            _initialCover = null;
            CoverPreview = null;
            _closed.Dispose();
        }
    }

    private static IReadOnlyList<AlbumTrackItem> BuildTrackItems(List<Music> tracks)
    {
        int maxDisk = 0;
        foreach (var music in tracks) if (music.DiskNumber > maxDisk) maxDisk = music.DiskNumber;
        bool multiDisc = maxDisk > 1;
        var items = new AlbumTrackItem[tracks.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            var music = tracks[i];
            string number = music.TrackNumber > 0
                ? multiDisc ? $"{music.DiskNumber}-{music.TrackNumber}" : music.TrackNumber.ToString()
                : "—";
            items[i] = new AlbumTrackItem(number,
                string.IsNullOrWhiteSpace(music.Title) ? "—" : music.Title,
                string.IsNullOrWhiteSpace(music.Author) ? "—" : music.Author,
                FormatDuration(music.Duration));
        }
        return items;
    }

    /// <summary>统计曲目艺术家（按分隔符拆分去重）与总时长，并生成 " · " 连接的原始艺术家展示行。</summary>
    private static (string Artists, int ArtistCount, TimeSpan Total) SummarizeTracks(List<Music> tracks)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        TimeSpan total = TimeSpan.Zero;
        foreach (var music in tracks)
        {
            total += music.Duration;
            if (string.IsNullOrEmpty(music.Author)) continue;
            if (seen.Add(music.Author)) ordered.Add(music.Author);
            foreach (var name in ArtistHelper.GetArtistNames(music.Author))
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }
        return (string.Join(" · ", ordered), names.Count, total);
    }

    private static string BuildSummary(int year, int trackCount, int artistCount, TimeSpan total)
    {
        string summary = year != 0 ? $"{year} · {trackCount} {NumberOfSongsText}" : $"{trackCount} {NumberOfSongsText}";
        if (artistCount >= 2) summary += $" · {artistCount} {NumberOfArtistsText}";
        return summary + " · " + FormatDuration(total);
    }

    /// <summary>展示包含小时的完整时长；空值显示占位。</summary>
    private static string FormatDuration(TimeSpan value) => value <= TimeSpan.Zero ? "—" : value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : value.ToString(@"mm\:ss");
}
