using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;

internal static class LoaderCancellationChecks
{
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
        public List<string> UILyrics { get => []; set => Publications++; }
    }
}
namespace WinUIMusicPlayer.Services
{
    // 解析器边界保留真实异步网络调用；本测试检查生产 LyricsLoader 的令牌和 UI 发布守卫。
    public sealed class LyricsRefreshService(LrcService service)
    {
        public int Calls;
        public async Task<List<string>> SetLyrics(Music music, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            try { await service.GetMixedLyricsAsync(music, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            return ["late lyrics"];
        }
    }
}
