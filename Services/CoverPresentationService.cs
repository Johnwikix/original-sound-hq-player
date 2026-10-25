using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services;

/// <summary>播放封面/调色板展示管线。后台工作受任务屏障管理，不持有页面或 AppViewModel。</summary>
public sealed class CoverPresentationService(AppState state, ApplicationTasks tasks, SystemMediaControlsService media,
    WebDavLibraryService webDav, ILogger<CoverPresentationService> logger) : IDisposable
{
    private sealed class CoverFallbackHolder(byte[]? bytes)
    {
        private byte[]? _bytes = bytes;

        public byte[]? Take() => Interlocked.Exchange(ref _bytes, null);

        public void Clear() => Interlocked.Exchange(ref _bytes, null);
    }

    private int _coverUpdateVersion, _defaultPaletteVersion;
    private bool _disposed, _started, _usesDefaultPalette;
    private CancellationTokenSource? _coverUpdateCts;
    private Music? _paletteMusic;
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        state.Preferences.PropertyChanged += OnCoverSettingsChanged;
    }
    public Task UpdatePlayBar(Music music, CancellationToken token = default)
    {
        if (_disposed || state.Lifecycle.Phase == AppPhase.Stopping) return Task.CompletedTask;

        var updateCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _coverUpdateCts, updateCts);
        previousCts?.Cancel();

        var scheduled = tasks.RunAsync(lifecycleToken =>
            RunCoverUpdateAsync(music, updateCts, lifecycleToken, token));

        // ApplicationTasks may already be closed during shutdown and therefore
        // not invoke the operation. Avoid leaving the per-update CTS alive.
        if (ReferenceEquals(scheduled, Task.CompletedTask))
        {
            Interlocked.CompareExchange(ref _coverUpdateCts, null, updateCts);
            updateCts.Dispose();
        }
        return scheduled;
    }

    private async Task RunCoverUpdateAsync(
        Music music,
        CancellationTokenSource updateCts,
        CancellationToken lifecycleToken,
        CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            updateCts.Token, lifecycleToken, callerToken);
        try
        {
            await UpdatePlayBarCoreAsync(music, linked.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _coverUpdateCts, null, updateCts);
            updateCts.Dispose();
        }
    }

    private async Task UpdatePlayBarCoreAsync(Music music, CancellationToken token)
    {
        if (_disposed || state.Lifecycle.Phase == AppPhase.Stopping) return;
        int version = Interlocked.Increment(ref _coverUpdateVersion);
        try
        {
            byte[]? picData = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                // 热缓存直接用于取色/大图展示，不再每次切歌都读取完整原图数组。
                if (music.ImageHash is { Length: > 0 })
                {
                    string cachedRaw = FindRawCachePath(music.ImageHash);
                    if (File.Exists(cachedRaw) && new FileInfo(cachedRaw).Length > 0 &&
                        (music.IsRemote || File.GetLastWriteTimeUtc(cachedRaw) > File.GetLastWriteTimeUtc(music.Path)))
                        return null;
                }
                return await GetRawImage(music, false, token);
            }, token);
            if (token.IsCancellationRequested) return;

            AnimatedWin2dControls.Impressionist.PaletteResult? palette = null;
            if (!string.IsNullOrEmpty(music.ImageHash))
            {
                string thumbPath = CoverLoadQueue.GetThumbCachePath(music.ImageHash, CoverLoadQueue.CoverSize);
                palette = await AnimatedWin2dControls.Impressionist.PaletteExtractor
                    .ExtractFromBmpCacheAsync(thumbPath, state.Preferences.PaletteAlgorithm, ct: token);
            }
            if (palette is null)
            {
                picData ??= await Task.Run(() => GetRawImage(music, false, token), token);
                if (picData is { Length: > 0 })
                    palette = await Task.Run(() =>
                        AnimatedWin2dControls.Impressionist.PaletteExtractor
                            .ExtractFromImageBytesAsync(picData, state.Preferences.PaletteAlgorithm, ct: token), token);
            }

            // 封面像素：仅供 RotatingMesh 背景着色器旋转层使用，其它着色器只取色、
            // 不做封面解码（非 RotatingMesh 模式零新增开销）。无封面时为 null，
            // 着色器回退到调色板渐变。
            AnimatedWin2dControls.Impressionist.ArtworkPixelData? artwork = null;
            if (state.Preferences.BackgroundShader == AnimatedWin2dControls.BackgroundShaderMode.RotatingMesh)
            {
                // 优先读缩略图 BMP 缓存（90KB，最快）；
                // 缓存缺失（首次播放/被清理）时直接用已读取的大图数据兜底。
                if (!string.IsNullOrEmpty(music.ImageHash))
                {
                    string artworkThumbPath = CoverLoadQueue.GetThumbCachePath(music.ImageHash, CoverLoadQueue.CoverSize);
                    artwork = await AnimatedWin2dControls.Impressionist.ArtworkPixelDecoder
                        .LoadSquareRgba8FromBmpCacheAsync(artworkThumbPath, ct: token);
                }

                if (artwork is null)
                {
                    picData ??= await Task.Run(() => GetRawImage(music, false, token), token);
                    if (picData is { Length: > 0 })
                        artwork = await AnimatedWin2dControls.Impressionist.ArtworkPixelDecoder
                            .LoadSquareRgba8FromImageBytesAsync(picData, ct: token);
                }
            }

            if (token.IsCancellationRequested) return;

            // 正常路径只保留共享文件；缓存失败时才保留原图/已有字节用于兜底。
            string? mediaCoverPath = null;
            byte[]? mediaFallbackCover = picData;
            if (music.ImageHash is { Length: > 0 })
            {
                string rawPath = FindRawCachePath(music.ImageHash);
                try
                {
                    if (File.Exists(rawPath) && new FileInfo(rawPath).Length > 0)
                    {
                        // 展示缓存生成失败仍可直接使用原图，字节也保留到实际打开文件后。
                        mediaCoverPath = rawPath;
                        bool hadDisplayCache = File.Exists(PlaybackCoverImage.GetCachePath(rawPath));
                        mediaCoverPath = await Task.Run(
                            () => PlaybackCoverImage.GetOrCreateAsync(rawPath, token), token);
                        mediaFallbackCover = null;
                        if (music.IsRemote && !hadDisplayCache && mediaCoverPath != rawPath)
                            await webDav.TrimCoverCacheAsync(rawPath, token);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "准备系统媒体封面缓存失败，保留原图兜底"); }
            }
            picData = null;

            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && (state.Lifecycle.Phase != AppPhase.Stopping) && !token.IsCancellationRequested && version == Volatile.Read(ref _coverUpdateVersion) &&
                    ReferenceEquals(music, state.Playback.CurrentPlayingMusic))
                {
                    _defaultPaletteVersion++;
                    _paletteMusic = music;
                    _usesDefaultPalette = palette is null;
                    state.Presentation.LyricPageBackgroundHash = music.ImageHash ?? "";
                    state.Presentation.LyricPagePalette = palette;
                    if (_usesDefaultPalette) _ = RefreshDefaultPaletteAsync(token);
                    state.Presentation.LyricPageArtwork = artwork;
                    state.Presentation.MusicInfo = $"{music.Extension} {music.SampleRate}Hz {music.BitDepth}bit {music.BitRate}kbps";
                }
            });

            // --- 阶段 D: 更新系统媒体控制 (SMTC) ---
            // 在 UI 线程提交；实际文件打开由媒体服务的后台任务处理。
            var fallbackHolder = new CoverFallbackHolder(mediaFallbackCover);
            mediaFallbackCover = null;
            var fallbackRegistration = token.Register(
                static holder => ((CoverFallbackHolder)holder!).Clear(), fallbackHolder);
            bool enqueued = App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var fallbackCover = fallbackHolder.Take();
                    if (_disposed || state.Lifecycle.Phase == AppPhase.Stopping || token.IsCancellationRequested || version != Volatile.Read(ref _coverUpdateVersion) ||
                        !ReferenceEquals(music, state.Playback.CurrentPlayingMusic)) return;

                    media.UpdateSystemMediaControlsState();
                    media.UpdateTimelineProperties(TimeSpan.Zero, music.Duration);
                    _ = media.UpdateMediaInfoFromFile(
                        music.Title,
                        music.Author,
                        music.Album,
                        mediaCoverPath,
                        fallbackCover);
                }
                finally
                {
                    fallbackHolder.Clear();
                    fallbackRegistration.Dispose();
                }
            });
            if (!enqueued)
            {
                fallbackHolder.Clear();
                fallbackRegistration.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"更新播放栏失败: {ex.Message}");
        }
    }

    public void ThemeChangedUpdateCover()
    {
        if (state.Playback.CurrentPlayingMusic is null) return;
        state.Preferences.IsDarkMode = state.Preferences.ThemeType switch { "Dark" => true, "Light" => false, _ => !GetIsLightTheme() };
    }

    private readonly DefaultCoverPaletteCache _defaultPalettes = new(LoadDefaultPaletteAsync);

    private static Task<AnimatedWin2dControls.Impressionist.PaletteResult?> LoadDefaultPaletteAsync(
        bool isDark, AnimatedWin2dControls.Impressionist.PaletteAlgorithm algorithm, CancellationToken token) =>
        Task.Run(async () =>
        {
            string name = isDark ? "default_cover_black.png" : "default_cover_white.png";
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            return await AnimatedWin2dControls.Impressionist.PaletteExtractor
                .ExtractFromImageBytesAsync(bytes, algorithm, ct: token);
        }, token);

    private void OnCoverSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(state.Preferences.IsDarkMode) or nameof(state.Preferences.PaletteAlgorithm))
            _ = RefreshDefaultPaletteAsync();
        else if (e.PropertyName == nameof(state.Preferences.MusicCoverCache) && state.Playback.CurrentPlayingMusic is { } music)
        {
            // 根目录切换后，即使歌曲标识不变，也要重新取得原图并刷新展示控件。
            state.Presentation.LyricPageBackgroundHash = "";
            _ = UpdatePlayBar(music);
        }
    }

    // UI 线程捕获状态；解码在后台，发布前再次核对，避免旧主题/旧歌曲覆盖当前结果。
    private Task RefreshDefaultPaletteAsync(CancellationToken token = default)
        => tasks.RunAsync(_ => RefreshDefaultPaletteCoreAsync(token));
    private async Task RefreshDefaultPaletteCoreAsync(CancellationToken token)
    {
        int version = ++_defaultPaletteVersion;
        var music = _paletteMusic;
        if (!_usesDefaultPalette || music is null || !ReferenceEquals(music, state.Playback.CurrentPlayingMusic)) return;
        bool isDark = state.Preferences.IsDarkMode;
        var algorithm = state.Preferences.PaletteAlgorithm;
        try
        {
            var palette = await _defaultPalettes.GetAsync(isDark, algorithm, token);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && (state.Lifecycle.Phase != AppPhase.Stopping) && !token.IsCancellationRequested && version == _defaultPaletteVersion && _usesDefaultPalette &&
                    ReferenceEquals(music, state.Playback.CurrentPlayingMusic) &&
                    isDark == state.Preferences.IsDarkMode && algorithm == state.Preferences.PaletteAlgorithm)
                    state.Presentation.LyricPagePalette = palette;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "默认封面取色失败"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var updateCts = Interlocked.Exchange(ref _coverUpdateCts, null);
        updateCts?.Cancel();
        // The running operation owns disposal; this only sends cancellation.
        Interlocked.Increment(ref _coverUpdateVersion);
        _defaultPaletteVersion++;
        state.Preferences.PropertyChanged -= OnCoverSettingsChanged;
        _paletteMusic = null;
        state.Presentation.LyricPagePalette = null;
        state.Presentation.LyricPageArtwork = null;
    }
}
