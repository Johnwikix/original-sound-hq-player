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
        using var covers = new CoverPresentationService(state, tasks, media,
            new WebDavLibraryService(), NullLogger<CoverPresentationService>.Instance);
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

            ToolUtils.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ToolUtils.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Playback.TransitionPresentation = new(bytesMusic);
            Check(state.Presentation.LyricPageBackgroundHash == "", "Preview should clear the outgoing cover immediately");
            await ToolUtils.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            state.Playback.TransitionPresentation = new(rawMusic);
            await WaitForDisplayedCoverAsync(rawMusic.ImageHash);
            ToolUtils.ContinueRead.TrySetResult();
            await Task.Delay(50);
            Check(state.Presentation.LyricPageBackgroundHash == rawMusic.ImageHash, "A superseded preview painted its cover");
            Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == normalMusic.Title,
                "A preview changed the committed system media track");

            state.Playback.TransitionPresentation = null;
            await WaitForDisplayedCoverAsync(normalMusic.ImageHash);

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

        async Task WaitForDisplayedCoverAsync(string hash, bool requirePalette = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (state.Presentation.LyricPageBackgroundHash != hash || (requirePalette && state.Presentation.LyricPagePalette is null))
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
