using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>
/// 外部文件打开（文件关联/打开方式入口）的唯一状态源：解析外部文件并尽量沉淀为库内条目——
/// 固定本地盘且库内无同路径行时入库（AddExternalFileAsync，归入"外部导入"虚拟文件夹），
/// 返回库内权威行，播放、统计、歌词、当前曲存档均为标准库内语义；可移动盘/网络盘/UNC 或
/// 入库失败时回退一次性播放（Id 保持 0，不写库不统计，歌词走 OneShotLyricsCache）。
/// 播放触发是事件驱动（生命周期与引擎就绪属性变化时派发），引擎未就绪的请求登记挂起，
/// 不存在固定延时等待；迟到回调按请求代差丢弃。
/// </summary>
public sealed class OneShotPlaybackService : IDisposable
{
    private readonly AppViewModel _appViewModel;
    private readonly AppLifecycle _lifecycle;
    private readonly MusicDatabaseService _databaseService;
    private readonly NotificationService _notificationService;
    private readonly ILogger<OneShotPlaybackService> _logger;
    private readonly object _gate = new();
    private string? _pendingPath;
    private Task<Music?>? _resolveTask;
    // 每次播放请求递增；已派发的迟到回调（被更新的打开请求覆盖）按代差丢弃。
    private int _generation;
    private int _dispatchedGeneration;
    private bool _disposed;

    public OneShotPlaybackService(
        AppViewModel appViewModel,
        AppLifecycle lifecycle,
        MusicDatabaseService databaseService,
        NotificationService notificationService,
        ILogger<OneShotPlaybackService> logger)
    {
        _appViewModel = appViewModel;
        _lifecycle = lifecycle;
        _databaseService = databaseService;
        _notificationService = notificationService;
        _logger = logger;
        _appViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _lifecycle.Changed += OnLifecycleChanged;
    }

    /// <summary>
    /// 启动最早期捕获激活文件路径：MSIX 文件激活走 AppLifecycle 激活参数；
    /// unpackaged（开发调试）下回退解析命令行。多选文件只取第一个受支持的音频文件。
    /// </summary>
    public static string? CaptureActivationPath()
    {
        try
        {
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated.Kind == ExtendedActivationKind.File
                && activated.Data is IFileActivatedEventArgs fileArgs)
            {
                foreach (var file in fileArgs.Files)
                {
                    if (IsPlayablePath(file.Path, out var path)) return path;
                }
            }
        }
        catch (Exception ex)
        {
            // 此处可能早于任何 DI 依赖就绪，只走诊断输出，不触碰 App.Services。
            System.Diagnostics.Debug.WriteLine($"读取文件激活参数失败: {ex.Message}");
        }
        // 打包应用的命令行不含 shell 传递的文件路径；此回退仅服务 unpackaged 调试场景。
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (IsPlayablePath(args[i], out var path)) return path;
            }
        }
        catch { /* 命令行不可用时静默跳过，不影响正常启动 */ }
        return null;

        static bool IsPlayablePath(string? path, out string result)
        {
            result = path ?? string.Empty;
            return result.Length > 0
                && ToolUtils.IsMusicFile(Path.GetExtension(result))
                && File.Exists(result);
        }
    }

    /// <summary>冷启动登记待播文件并立即开始解析；在 OnLaunched 早期调用，与 Host/IPC/数据库初始化并行。</summary>
    public void Begin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        lock (_gate)
        {
            if (_disposed || _pendingPath is not null) return;
            _pendingPath = path;
            _resolveTask = ResolveAsync(path);
            _generation = 1;
        }
        TryDispatchPending();
    }

    /// <summary>暖启动（收到第二实例转发的路径）请求播放：就绪则解析完成后立即播放，否则登记待引擎就绪。重复打开同一路径视为重播。</summary>
    public void PlayNow(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        lock (_gate)
        {
            if (_disposed) return;
            // 同一路径也可能已移出曲库或上次解析失败；每个显式打开请求重新确认库内身份。
            _pendingPath = path;
            _resolveTask = ResolveAsync(path);
            _generation++;
        }
        TryDispatchPending();
    }

    /// <summary>引擎首推前的探询：仅在解析已完成且成功时返回结果（固定盘为已入库行，其余为未入库实例），
    /// 供内核直接加载（省一次恢复曲加载）。</summary>
    public Music? TryGetResolvedMusic()
    {
        lock (_gate)
        {
            return _resolveTask is { IsCompletedSuccessfully: true } task ? task.Result : null;
        }
    }

    private async Task<Music?> ResolveAsync(string path)
    {
        // 固定盘先查库：已在库内的文件直接返回权威行，免去重复元数据解析。
        bool fixedDrive = LibraryPath.IsFixedLocalDrive(path);
        if (fixedDrive)
        {
            try
            {
                // 查库须等数据库就绪（Begin 早于 Host/数据库初始化）。
                await _databaseService.WhenInitialized.ConfigureAwait(false);
                var existing = await _databaseService.FindMusicByPathAsync(path).ConfigureAwait(false);
                if (existing is not null) return existing;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "外部文件查库失败，按未入库继续解析: {Path}", path);
            }
        }
        Music? music;
        string? lyrics;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            (music, lyrics) = await ToolUtils.GetMusicInfo(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析外部音乐文件失败: {Path}", path);
            return null;
        }
        if (music is null) return null;
        // 可移动/网络/UNC 等非固定盘不沉淀曲库（与 UsbDeviceMusic 设备音乐体系保持边界）：
        // 携带内嵌歌词走一次性播放。
        if (!fixedDrive) return WithEmbeddedLyrics(music, lyrics);
        try
        {
            // 导入成功返回库内权威行（Id>0）；并发方先落库时由其内部按路径回查返回权威行。
            var imported = await _databaseService.AddExternalFileAsync(music, lyrics ?? string.Empty).ConfigureAwait(false);
            if (imported is not null)
            {
                await MigrateOneShotLyricsAsync(imported, lyrics).ConfigureAwait(false);
                return imported;
            }
            _logger.LogWarning("外部文件入库未生效，回退一次性播放: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "外部文件入库失败，回退一次性播放: {Path}", path);
        }
        return WithEmbeddedLyrics(music, lyrics);
    }

    private static Music WithEmbeddedLyrics(Music music, string? lyrics)
    {
        // 内嵌歌词随 Music 对象在内存传递（LyricsRefreshService 对未入库曲目优先使用）。
        if (!string.IsNullOrEmpty(lyrics)) music.EmbeddedLyrics = lyrics;
        return music;
    }

    /// <summary>导入入库后把旧一次性歌词缓存迁入 MusicLyrics（仅当无内嵌且库内歌词为空），
    /// 避免升级场景下同一文件重复在线搜索；失败只记录不影响播放。缓存条目保留，由其上限淘汰。</summary>
    private async Task MigrateOneShotLyricsAsync(Music imported, string? embeddedLyrics)
    {
        if (!string.IsNullOrEmpty(embeddedLyrics)) return;
        try
        {
            var (lyrics, _, krc, _) = await _databaseService.GetLyricsAsync(imported.Id).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(lyrics) || !string.IsNullOrEmpty(krc)) return;
            var cached = OneShotLyricsCache.Load(imported.Path);
            if (cached is null) return;
            if (string.IsNullOrEmpty(cached.Lrc) && string.IsNullOrEmpty(cached.Krc)) return;
            await _databaseService.SaveLyricsAsync(imported.Id, cached.Lrc, cached.Trans, cached.Krc, cached.TKrc)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "迁移一次性歌词缓存失败（忽略）: {Path}", imported.Path);
        }
    }

    // 就绪 = 生命周期 Ready 且引擎就绪；两个状态源变化时都重新评估，覆盖启动期任一先到的顺序。
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.IsPlaybackEngineReady)) TryDispatchPending();
    }

    private void OnLifecycleChanged(object? sender, EventArgs e) => TryDispatchPending();

    private void TryDispatchPending()
    {
        lock (_gate)
        {
            if (_disposed || _pendingPath is null || _resolveTask is null) return;
            if (_dispatchedGeneration == _generation) return;
            if (!_lifecycle.IsReady || !_appViewModel.IsPlaybackEngineReady) return;
            _dispatchedGeneration = _generation;
        }
        _ = PlayPendingAsync();
    }

    private async Task PlayPendingAsync()
    {
        string path;
        Task<Music?> resolve;
        int generation;
        lock (_gate)
        {
            path = _pendingPath!;
            resolve = _resolveTask!;
            generation = _generation;
        }
        try
        {
            // 解析结果是唯一事实源：固定盘文件（新导入或库内已有）返回库内行（Id>0），
            // 统一走库内播放链路（统计/歌词/当前曲存档为标准库内语义）；非固定盘或入库
            // 失败返回 Id=0 实例，走一次性播放。禁止在派发时另行查库判定——解析中的
            // 入库写仍在飞行，查空会误入一次性分支（不发布列表、不建文件夹队列）。
            Music? music = await resolve.ConfigureAwait(false);
            if (music is null)
            {
                NotifyFailure(path);
                return;
            }
            bool isLibraryRow = music.Id > 0;
            // 首次入库的条目增量发布到内存索引、文件夹计数与页面投影（先于播放派发完成，保证随后同帧可见）。
            if (isLibraryRow) await PublishImportedOnUiAsync(music).ConfigureAwait(false);
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                lock (_gate)
                {
                    // 迟到守卫：已被更新一次的打开请求覆盖则丢弃，避免双重播放。
                    if (_disposed || generation != _generation) return;
                }
                if (!_lifecycle.IsReady || !_appViewModel.IsPlaybackEngineReady) return;
                // 库内行优先用 SongsSource 实例播放，与库内列表/收藏等状态保持同源；
                // 条目尚未同步进内存索引（如发布失败）时退回数据库行实例。
                Music target = isLibraryRow ? (_appViewModel.FindById(music.Id) ?? music) : music;
                var browse = App.Services.GetRequiredService<MusicBrowseViewModel>();
                if (isLibraryRow) browse.PlayMusicWithFolderQueue(target);
                else _ = browse.PlayMusic(target);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "一次性播放外部文件失败: {Path}", path);
        }
    }

    /// <summary>首次入库的导入条目发布到内存索引与全部页面投影。发布在 UI 线程做存在性检查，
    /// 同步 SongsSource/ListSongs 并触发 NotifySongsSourceChanged——歌曲/专辑/艺术家等投影
    /// 按库版本重建，再从数据库同步文件夹行与计数；不必等重启后的全量加载。
    /// 已在索引的歌曲不重复发布，发布失败不影响播放。</summary>
    private Task PublishImportedOnUiAsync(Music dbMusic) =>
        App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                }
                var folders = App.Services.GetRequiredService<AddFolderViewModel>();
                await folders.PublishImportedAsync(dbMusic);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "发布外部导入条目到界面失败（不影响播放）: {Path}", dbMusic.Path);
            }
        });

    private void NotifyFailure(string path)
    {
        _logger.LogWarning("外部文件无法解析或播放: {Path}", path);
        try
        {
            _notificationService.SendNotification(
                ToolUtils.GetString("OneShotOpenFailedTitle"),
                string.Format(ToolUtils.GetString("OneShotOpenFailedContent"), Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送一次性播放失败通知出错");
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _appViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _lifecycle.Changed -= OnLifecycleChanged;
    }
}
