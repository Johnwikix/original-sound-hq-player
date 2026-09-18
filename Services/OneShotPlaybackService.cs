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
/// 外部文件一次性播放（文件关联/打开方式入口）的唯一状态源：解析出未入库的 Music（Id 保持 0）
/// 并仅替换 CurrentPlayingMusic 播放，不改播放列表、不写数据库与播放统计。
/// 播放触发是事件驱动（生命周期与引擎就绪属性变化时派发），引擎未就绪的请求登记挂起，不存在固定延时等待。
/// </summary>
public sealed class OneShotPlaybackService : IDisposable
{
    private readonly AppViewModel _appViewModel;
    private readonly AppLifecycle _lifecycle;
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
        NotificationService notificationService,
        ILogger<OneShotPlaybackService> logger)
    {
        _appViewModel = appViewModel;
        _lifecycle = lifecycle;
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
            if (!string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase) || _resolveTask is null)
            {
                _pendingPath = path;
                _resolveTask = ResolveAsync(path);
            }
            _generation++;
        }
        TryDispatchPending();
    }

    /// <summary>引擎首推前的探询：仅在解析已完成且成功时返回外部文件，供内核直接加载（省一次恢复曲加载）。</summary>
    public Music? TryGetResolvedMusic()
    {
        lock (_gate)
        {
            return _resolveTask is { IsCompletedSuccessfully: true } task ? task.Result : null;
        }
    }

    private async Task<Music?> ResolveAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var (music, lyrics) = await ToolUtils.GetMusicInfo(file);
            // 内嵌歌词随 Music 对象在内存传递（LyricsRefreshService 对未入库曲目优先使用）。
            if (music is not null && !string.IsNullOrEmpty(lyrics))
                music.EmbeddedLyrics = lyrics;
            // 未入库曲目 Id 保持 0：统计会话与 LastPlayedMusicId 持久化据此跳过。
            return music;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析外部音乐文件失败: {Path}", path);
            return null;
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
            var music = await resolve.ConfigureAwait(false);
            if (music is null)
            {
                NotifyFailure(path);
                return;
            }
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                lock (_gate)
                {
                    // 迟到守卫：已被更新一次的打开请求覆盖则丢弃，避免双重播放。
                    if (_disposed || generation != _generation) return;
                }
                if (!_lifecycle.IsReady || !_appViewModel.IsPlaybackEngineReady) return;
                _ = App.Services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(music);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "一次性播放外部文件失败: {Path}", path);
        }
    }

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
