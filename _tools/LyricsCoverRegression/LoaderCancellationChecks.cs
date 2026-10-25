using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;

internal static class LoaderCancellationChecks
{
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
            Handler = (music, _, _) => Task.FromResult(new List<string> { music.Title })
        };
        using var loader = new LyricsLoader(state, parser, tasks, NullLogger<LyricsLoader>.Instance);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            loader.Load(new Music { Title = "A" });
            await Task.Delay(100);
            loader.Load(new Music { Title = "B" });
            await Task.Delay(150);
            loader.Load(new Music { Title = "C" });
            await Task.Delay(200);
            if (parser.Calls != 0 || state.Presentation.Publications != 0)
                throw new Exception("500 ms 防抖到期前已经解析或发布歌词");
            await Task.Delay(450);
            if (parser.Calls != 1 || state.Presentation.LastLyrics.Single() != "C")
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
                    cached?.Invoke(["Stale cache"]);
                }
                return [music.Title];
            };
            loader.Load(new Music { Title = "Old in-flight" });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            loader.Load(new Music { Title = "Newest" });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(650);
            int publications = state.Presentation.Publications;
            if (state.Presentation.LastLyrics.Single() != "Newest") throw new Exception("新歌词被旧请求阻塞");
            releaseOld.TrySetResult();
            await Task.Delay(100);
            if (state.Presentation.Publications != publications) throw new Exception("取消后仍发布旧缓存或最终结果");

            int calls = parser.Calls;
            loader.Load(new Music { Title = "Pending at shutdown" });
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
        loader.Load(new Music());
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
    }
    public sealed class TestPresentation
    {
        public int LastLyricIndex { get; set; }
        public int Publications { get; private set; }
        public List<string> LastLyrics { get; private set; } = [];
        public List<string> UILyrics
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
        public Func<Music, CancellationToken, Action<List<string>>?, Task<List<string>>>? Handler;
        public async Task<List<string>> SetLyrics(Music music, CancellationToken token, Action<List<string>>? publishCached = null)
        {
            Interlocked.Increment(ref Calls);
            if (Handler is not null) return await Handler(music, token, publishCached);
            try { await service.GetMixedLyricsAsync(music, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return ["late lyrics"];
        }
    }
}
