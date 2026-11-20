using AnimatedWin2dControls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;

internal static class CoverPipelineRegression
{
    internal static async Task RunAsync()
    {
        var lifecycle = new AppLifecycle();
        var state = new AppState(lifecycle);
        var tasks = new ApplicationTasks(lifecycle);
        using var media = new SystemMediaControlsService(NullLogger<SystemMediaControlsService>.Instance);
        media.Initialize(new PlaybackCommands());
        var coverLogger = new CoverLogger();
        using var covers = new CoverPresentationService(state, tasks, media,
            new WebDavLibraryService(), coverLogger);
        covers.Start();

        string raw = ToolUtils.FindRawCachePath("pipeline_raw");
        File.Copy(ToolUtils.FindRawCachePath("portrait"), raw, true);
        string blockedDisplay = PlaybackCoverImage.GetCachePath(raw);
        Directory.CreateDirectory(blockedDisplay);
        string blockedRaw = ToolUtils.FindRawCachePath("pipeline_bytes");
        Directory.CreateDirectory(blockedRaw);
        try
        {
            var rawMusic = new Music { ImageHash = "pipeline_raw", Path = raw + ".flac", Title = "raw fallback" };
            var bytesMusic = new Music { ImageHash = "pipeline_bytes", Path = ToolUtils.FindRawCachePath("small"), Title = "bytes fallback" };
            var normalMusic = new Music { ImageHash = "large", Path = raw + ".flac", Title = "normal cache" };
            await ShowAsync(rawMusic, 800, 2400);
            Check(ToolUtils.RawReads == 0, "展示缓存失败不应重读原图数组");
            await ShowAsync(bytesMusic, 320, 200);
            Check(ToolUtils.StoreFailures == 1, "原图写入失败场景未触发");

            int reads = ToolUtils.RawReads;
            await ShowAsync(normalMusic, 1536, 1536);
            Check(ToolUtils.RawReads == reads, "正常热缓存新增了原图数组读取");

            await CheckPreviewContinuityAsync(normalMusic, rawMusic, bytesMusic);

            // 预览已完成后，交接只补发系统媒体封面。
            state.Playback.TransitionPresentation = new(rawMusic);
            await WaitForDisplayedCoverAsync(rawMusic.ImageHash, requirePalette: true);
            int thumbnailReads = CoverLoadQueue.ThumbnailReads;
            state.Playback.CurrentPlayingMusic = rawMusic;
            state.Playback.TransitionPresentation = null;
            await ShowAsync(rawMusic, 800, 2400);
            Check(CoverLoadQueue.ThumbnailReads == thumbnailReads, "A completed preview decoded the palette again at commit");

            // 预览已进入原图读取时，交接接续该请求，并复用其字节兜底。
            reads = ToolUtils.RawReads;
            ToolUtils.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolUtils.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Playback.TransitionPresentation = new(bytesMusic);
            await ToolUtils.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            state.Playback.CurrentPlayingMusic = bytesMusic;
            state.Playback.TransitionPresentation = null;
            var committed = covers.UpdatePlayBar(bytesMusic);
            ToolUtils.ContinueRead.TrySetResult();
            await committed;
            await WaitForMediaAsync(bytesMusic, 320, 200);
            Check(ToolUtils.RawReads == reads + 1 && CoverLoadQueue.ThumbnailReads == thumbnailReads + 1,
                "An in-flight preview restarted cover preparation at commit");

            // 保留真实异步获取时序，停在原图返回前，再切到新歌。
            ToolUtils.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolUtils.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Playback.CurrentPlayingMusic = bytesMusic;
            var stale = covers.UpdatePlayBar(bytesMusic);
            await ToolUtils.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ShowAsync(rawMusic, 800, 2400);
            ToolUtils.ContinueRead.TrySetResult();
            await stale;
            await Task.Delay(50);
            Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == rawMusic.Title,
                "已取消的字节兜底覆盖了新歌");

            ToolUtils.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolUtils.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Playback.CurrentPlayingMusic = bytesMusic;
            var stopping = covers.UpdatePlayBar(bytesMusic);
            await ToolUtils.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lifecycle.TryBeginExit(out _);
            await tasks.DrainAsync();
            Check(stopping.IsCompleted, "退出未排空封面工作");
            await media.StopAsync();
            Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == rawMusic.Title,
                "退出后仍发布了封面");
            media.Dispose();
            using var exclusive = new FileStream(raw, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            ToolUtils.ContinueRead?.TrySetResult();
            ToolUtils.ReadStarted = null;
            ToolUtils.ContinueRead = null;
            Directory.Delete(blockedDisplay);
            Directory.Delete(blockedRaw);
        }

        async Task CheckPreviewContinuityAsync(Music outgoing, Music next, Music blocked)
        {
            state.Preferences.BackgroundShader = BackgroundShaderMode.RotatingMesh;
            await ShowAsync(outgoing, 1536, 1536);
            var presentation = state.Presentation;
            var outgoingPalette = presentation.LyricPageBackdrop.Palette;
            var outgoingArtwork = presentation.LyricPageBackdrop.Artwork;
            Check(outgoingPalette is not null && outgoingArtwork is not null, "The outgoing shader fixture needs real artwork");

            var hashes = new List<string>();
            int backdropUpdates = 0;
            bool blankPalette = false;
            bool blankArtwork = false;
            void TrackPublication(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(presentation.LyricPageBackgroundHash))
                    hashes.Add(presentation.LyricPageBackgroundHash);
                else if (args.PropertyName == nameof(presentation.LyricPageBackdrop))
                {
                    backdropUpdates++;
                    blankPalette |= presentation.LyricPageBackdrop.Palette is null;
                    blankArtwork |= presentation.LyricPageBackdrop.Artwork is null;
                }
            }

            presentation.PropertyChanged += TrackPublication;
            try
            {
                var failed = new Music { Title = "failed preview", Path = raw + ".missing" };
                state.Playback.TransitionPresentation = new(failed);
                await coverLogger.LoadFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(StillOutgoing(), "A failed preview cleared the outgoing foreground or shader");

                ArmRead();
                state.Playback.TransitionPresentation = new(blocked);
                await ToolUtils.ReadStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(StillOutgoing(), "A pending preview cleared the outgoing foreground or shader");
                state.Playback.TransitionPresentation = null;
                ToolUtils.ContinueRead!.TrySetResult();
                Check(StillOutgoing() && hashes.Count == 0 && backdropUpdates == 0, "Rollback published a placeholder or changed the outgoing scene");

                ArmRead();
                state.Playback.TransitionPresentation = new(blocked);
                await ToolUtils.ReadStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                state.Playback.TransitionPresentation = new(next);
                await WaitForDisplayedCoverAsync(next.ImageHash, requirePalette: true);
                ToolUtils.ContinueRead!.TrySetResult();
                await Task.Delay(50);
                Check(hashes.SequenceEqual([next.ImageHash]) && backdropUpdates == 1 && !blankPalette && !blankArtwork,
                    "Rapid previews published a default foreground or reset the shader; only the final cover should be published");
                Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == outgoing.Title,
                    "A preview changed the committed system media track");

                var nextPalette = presentation.LyricPageBackdrop.Palette;
                var nextArtwork = presentation.LyricPageBackdrop.Artwork;
                ArmRead();
                state.Playback.TransitionPresentation = new(new Music { Title = "no cover" });
                await ToolUtils.ReadStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(presentation.LyricPageBackgroundHash == next.ImageHash
                    && ReferenceEquals(presentation.LyricPageBackdrop.Palette, nextPalette)
                    && ReferenceEquals(presentation.LyricPageBackdrop.Artwork, nextArtwork),
                    "The shader switched to defaults before the no-cover track was resolved");
                ToolUtils.ContinueRead!.TrySetResult();
                await WaitForDisplayedCoverAsync("", requirePalette: true);
                Check(hashes.SequenceEqual([next.ImageHash, ""]) && backdropUpdates == 2
                    && !blankPalette && presentation.LyricPageBackdrop.Artwork is null,
                    "A no-cover track must publish a ready fallback palette and clear artwork once");
            }
            finally
            {
                presentation.PropertyChanged -= TrackPublication;
                ToolUtils.ContinueRead?.TrySetResult();
            }

            state.Playback.TransitionPresentation = null;
            await WaitForDisplayedCoverAsync(outgoing.ImageHash, requirePalette: true);
            state.Playback.CurrentPlayingMusic = null;
            Check(presentation.LyricPageBackgroundHash.Length == 0 && presentation.LyricPageBackdrop.Palette is null
                && presentation.LyricPageBackdrop.Artwork is null && presentation.MusicInfo.Length == 0,
                "Removing the displayed track did not clear its presentation");
            state.Preferences.BackgroundShader = default;
            await ShowAsync(outgoing, 1536, 1536);

            bool StillOutgoing() => presentation.LyricPageBackgroundHash == outgoing.ImageHash
                && ReferenceEquals(presentation.LyricPageBackdrop.Palette, outgoingPalette)
                && ReferenceEquals(presentation.LyricPageBackdrop.Artwork, outgoingArtwork)
                && !blankPalette && !blankArtwork;
        }

        static void ArmRead()
        {
            ToolUtils.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolUtils.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        async Task WaitForDisplayedCoverAsync(string hash, bool requirePalette = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (state.Presentation.LyricPageBackgroundHash != hash || (requirePalette && state.Presentation.LyricPageBackdrop.Palette is null))
                await Task.Delay(10, timeout.Token);
        }

        async Task ShowAsync(Music music, uint width, uint height)
        {
            state.Playback.CurrentPlayingMusic = music;
            await covers.UpdatePlayBar(music);
            await WaitForMediaAsync(music, width, height);
        }

        async Task WaitForMediaAsync(Music music, uint width, uint height)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (media.SystemMediaControls.DisplayUpdater.Type != Windows.Media.MediaPlaybackType.Music ||
                media.SystemMediaControls.DisplayUpdater.MusicProperties.Title != music.Title)
            {
                if (DateTime.UtcNow > timeout) throw new TimeoutException("等待 SMTC 封面发布超时");
                await Task.Delay(10);
            }
            using var thumbnail = await media.SystemMediaControls.DisplayUpdater.Thumbnail.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            Check(decoder.OrientedPixelWidth == width && decoder.OrientedPixelHeight == height,
                $"{music.Title} 封面内容不符: {decoder.OrientedPixelWidth}x{decoder.OrientedPixelHeight}");
        }
    }

    private sealed class CoverLogger : ILogger<CoverPresentationService>
    {
        public TaskCompletionSource LoadFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) LoadFailed.TrySetResult();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
