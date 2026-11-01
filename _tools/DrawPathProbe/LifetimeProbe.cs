using System.Diagnostics;
using System.Text.Json;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;

internal sealed partial class ProbeApplication
{
    /// <summary>有界自然 GC 测量；单独记录最终清理后的强制回收快照，不能将其混入播放样本。</summary>
    private async Task RunLifetimeProbeAsync(string resultPath)
    {
        var options = Environment.GetCommandLineArgs();
        int duration = int.Parse(options.FirstOrDefault(x => x.StartsWith("--seconds="))?[10..] ?? "300");
        if (duration is < 30 or > 3600) throw new ArgumentOutOfRangeException(nameof(duration));
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        long frames = 0;
        Exception? failure = null;
        var target = new CanvasRenderTarget(_canvas, 1100, 700);
        await _canvas.RunOnGameLoopThreadAsync(() => ConfigureLyrics(true));
        var update = FrameEventSubscription.SubscribeUpdate(_canvas, elapsed =>
        {
            try
            {
                IsPlayingBus.Publish(true);
                TimeProgressBus.Publish(clock.ElapsedMilliseconds % 110000);
                _lyrics.OnUpdate(_canvas, elapsed);
            }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
        });
        var draw = FrameEventSubscription.SubscribeDraw(_canvas, context =>
        {
            try
            {
                // 每帧创建并 Close/Release 原生会话、layer，覆盖 GPU 资源作用域与真实字形缓存。
                using (var session = CanvasDrawingSessionScope.Create(target))
                {
                    var intermediate = session.Context;
                    intermediate.Clear(Microsoft.UI.Colors.Transparent);
                    using var layer = intermediate.CreateLayer(0.9f);
                    _lyrics.OnDraw(_canvas, intermediate);
                }
                context.Clear(Microsoft.UI.Colors.Transparent);
                context.DrawImage(target);
                Interlocked.Increment(ref frames);
            }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
        });
        void Sample(string phase)
        {
            process.Refresh();
            var info = GC.GetGCMemoryInfo();
            samples.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds, Frames = Interlocked.Read(ref frames),
                PrivateBytes = process.PrivateMemorySize64, WorkingSet = process.WorkingSet64, Handles = process.HandleCount,
                ManagedBytes = GC.GetTotalMemory(false), GcCommitted = info.TotalCommittedBytes,
                Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
                PauseMs = GC.GetTotalPauseDuration().TotalMilliseconds });
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "RUNNING", Pid = process.Id, Samples = samples }));
        }
        try
        {
            _canvas.Paused = false;
            Sample("start");
            for (int second = 1; second <= duration; second++)
            {
                await Task.Delay(1000);
                if (failure is not null) throw failure;
                if (second % 3 == 0)
                    await _canvas.RunOnGameLoopThreadAsync(() => ConfigureLyrics(true));
                if (second % 10 == 0) Sample("natural");
            }
        }
        finally
        {
            _canvas.Paused = true;
            draw.Dispose();
            update.Dispose();
            _lyrics.PrepareForShutdown();
            target.Dispose();
        }
        if (frames < duration * 10) throw new InvalidOperationException("Insufficient actual drawing callbacks.");
        Sample("released");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Sample("after-explicit-gc");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "PASS", Pid = process.Id, DurationSeconds = duration,
            Note = "Production Win2D renderer on standard .NET runtime; republish every 3 seconds. Natural GC samples exclude the final explicit ownership cleanup check. Process memory is not a native-heap or per-process GPU allocation breakdown.",
            Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
