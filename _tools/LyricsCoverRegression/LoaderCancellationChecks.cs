using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;

internal static class LoaderCancellationChecks
{
    internal static List<LyricLine> CreateLyrics(string text) =>
        [new() { Words = [new() { Word = text }] }];

    public static void RunSwitching(LrcService service)
    {
        var previous = SynchronizationContext.Current;
        using var context = new UiContext();
        SynchronizationContext.SetSynchronizationContext(context);
        WinUIMusicPlayer.App.MainWindow = new(new(context));
        try
        {
            var test = CheckSwitchingAsync(service);
            while (!test.IsCompleted) context.Pump();
            test.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task CheckSwitchingAsync(LrcService service)
    {
        var state = new WinUIMusicPlayer.State.AppState();
        var tasks = new ApplicationTasks(state.Lifecycle);
        var parser = new LyricsRefreshService(service)
        {
            Handler = (music, _, _) => Task.FromResult(CreateLyrics(music.Title))
        };
        using var loader = new LyricsLoader(state, parser, tasks, NullLogger<LyricsLoader>.Instance);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Load(string title)
        {
            var music = new Music { Title = title };
            state.Playback.DisplayedPlaybackMusic = music;
            loader.Load(music);
        }

        try
        {
            Load("A");
            await Task.Delay(100);
            Load("B");
            await Task.Delay(150);
            Load("C");
            await Task.Delay(200);
            if (parser.Calls != 0 || state.Presentation.Publications != 0)
                throw new Exception("500 ms 防抖到期前已经解析或发布歌词");
            await Task.Delay(450);
            if (parser.Calls != 1 || state.Presentation.LastLyrics.Single().Words.Single().Word != "C")
                throw new Exception("快速切歌没有仅加载最后一首");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parser.Handler = async (music, token, cached) =>
            {
                if (music.Title == "Old in-flight")
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    started.TrySetResult();
                    await releaseOld.Task; // Keep real work alive after cancellation, as an uncooperative provider may do.
                    cached?.Invoke(CreateLyrics("Stale cache"));
                }
                return CreateLyrics(music.Title);
            };
            Load("Old in-flight");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Load("Newest");
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(650);
            int publications = state.Presentation.Publications;
            if (state.Presentation.LastLyrics.Single().Words.Single().Word != "Newest") throw new Exception("新歌词被旧请求阻塞");
            releaseOld.TrySetResult();
            await Task.Delay(100);
            if (state.Presentation.Publications != publications) throw new Exception("取消后仍发布旧缓存或最终结果");

            var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishPreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parser.Handler = async (music, _, cached) =>
            {
                if (music.Title == "First preview")
                {
                    previewStarted.TrySetResult();
                    await finishPreview.Task;
                    cached?.Invoke(CreateLyrics("Stale preview"));
                }
                return CreateLyrics(music.Title);
            };
            try
            {
                var preview = new Music { Title = "First preview" };
                state.Playback.DisplayedPlaybackMusic = preview;
                loader.ShowTransitionTrack(preview);
                if (state.Presentation.LastLyrics.Count != 0) throw new Exception("Preview retained outgoing lyrics.");
                await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var latest = new Music { Title = "Latest preview" };
                state.Playback.DisplayedPlaybackMusic = latest;
                loader.ShowTransitionTrack(latest);
                loader.Load(new Music { Title = "Outgoing editor completion" });
                await WaitForLyricsAsync(latest.Title);
                finishPreview.TrySetResult();
                await Task.Delay(100);
                if (state.Presentation.LastLyrics.Single().Words.Single().Word != latest.Title)
                    throw new Exception("An older preview or outgoing reload replaced the latest lyrics.");

                var rollback = new Music { Title = "Rollback" };
                state.Playback.DisplayedPlaybackMusic = rollback;
                loader.ShowTransitionTrack(rollback);
                await WaitForLyricsAsync(rollback.Title);
            }
            finally { finishPreview.TrySetResult(); }

            int calls = parser.Calls;
            Load("Pending at shutdown");
            loader.Dispose();
            state.Lifecycle.TryBeginExit(out _);
            await tasks.DrainAsync();
            if (parser.Calls != calls) throw new Exception("退出时仍执行防抖中的歌词请求");
        }
        finally
        {
            releaseOld.TrySetResult();
            loader.Dispose();
            state.Lifecycle.TryBeginExit(out _);
            await tasks.DrainAsync();
        }

        async Task WaitForLyricsAsync(string title)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (state.Presentation.LastLyrics.Count != 1 || state.Presentation.LastLyrics[0].Words.Single().Word != title)
                await Task.Delay(10, timeout.Token);
        }
    }

    public static void Run(LrcService service, PendingHandler handler)
    {
        var previous = SynchronizationContext.Current;
        using var context = new UiContext();
        SynchronizationContext.SetSynchronizationContext(context);
        WinUIMusicPlayer.App.MainWindow = new(new(context));
        try
        {
            var test = CheckAsync(service, handler);
            while (!test.IsCompleted) context.Pump();
            test.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static async Task CheckAsync(LrcService service, PendingHandler handler)
    {
        var state = new WinUIMusicPlayer.State.AppState();
        var tasks = new ApplicationTasks(state.Lifecycle);
        var parser = new LyricsRefreshService(service);
        var loader = new LyricsLoader(state, parser, tasks, NullLogger<LyricsLoader>.Instance);
        var music = new Music();
        state.Playback.DisplayedPlaybackMusic = music;
        loader.Load(music);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Lifecycle.TryBeginExit(out _);
        var drain = tasks.DrainAsync();
        try
        {
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (drain.IsCompleted) throw new Exception("未等待底层请求清理");
        }
        finally { handler.Release.TrySetResult(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        if (!handler.Finished || handler.Requests != 1 || state.Presentation.Publications != 0)
            throw new Exception("退出后仍回退请求或发布歌词");
        loader.Load(new Music());
        if (parser.Calls != 1) throw new Exception("停止后仍接受加载");
    }
}

internal sealed class UiContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
    public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));
    public void Pump()
    {
        if (_work.TryTake(out var work, 100)) work.Callback(work.State);
    }
    public void Dispose() => _work.Dispose();
}

namespace WinUIMusicPlayer
{
    internal static class App
    {
        public static TestWindow MainWindow { get; set; } = null!;
    }
    internal sealed record TestWindow(TestDispatcher DispatcherQueue);
    internal sealed class TestDispatcher(UiContext context)
    {
        public bool HasThreadAccess => ReferenceEquals(SynchronizationContext.Current, context);
        public void TryEnqueue(Action action) => context.Post(_ => action(), null);
    }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState
    {
        public AppLifecycle Lifecycle { get; } = new();
        public TestPresentation Presentation { get; } = new();
        public TestPlayback Playback { get; } = new();
    }
    public sealed class TestPlayback
    {
        public Music? DisplayedPlaybackMusic { get; set; }
    }

    public sealed class TestPresentation
    {
        public int LastLyricIndex { get; set; }
        public int Publications { get; private set; }
        public List<LyricLine> LastLyrics { get; private set; } = [];
        public List<LyricLine> UILyrics
        {
            get => LastLyrics;
            set
            {
                if (!WinUIMusicPlayer.App.MainWindow.DispatcherQueue.HasThreadAccess)
                    throw new Exception("歌词发布离开了 UI 线程");
                LastLyrics = value;
                Publications++;
            }
        }
    }
}
namespace WinUIMusicPlayer.Services
{
    // 解析器边界保留真实异步网络调用；本测试检查生产 LyricsLoader 的令牌和 UI 发布守卫。
    public sealed class LyricsRefreshService(LrcService service)
    {
        public int Calls;
        public Func<Music, CancellationToken, Action<List<LyricLine>>?, Task<List<LyricLine>>>? Handler;
        public async Task<List<LyricLine>> SetLyrics(Music music, CancellationToken token, Action<List<LyricLine>>? publishCached = null)
        {
            Interlocked.Increment(ref Calls);
            if (Handler is not null) return await Handler(music, token, publishCached);
            try { await service.GetMixedLyricsAsync(music, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return LoaderCancellationChecks.CreateLyrics("late lyrics");
        }
    }
}

namespace WinUIMusicPlayer.Utils
{
    internal static class ToolUtils
    {
        internal static string GetString(string key) => key;
    }
}
