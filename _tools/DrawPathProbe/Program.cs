using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;

/// <summary>Runs real WinUI/Win2D allocation measurements without audio or the user's library.</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ProbeApplication();
        });
    }
}

/// <summary>Marks measured phases so EventPipe allocation samples can be attributed separately.</summary>
[EventSource(Name = "MusicPlayer-DrawPathProbe")]
internal sealed class ProbeEvents : EventSource
{
    internal static readonly ProbeEvents Log = new();
    [Event(1)] public void PhaseStart(string name) => WriteEvent(1, name);
    [Event(2)] public void PhaseStop(string name) => WriteEvent(2, name);
}

/// <summary>Hosts production renderers and separates callback-boundary, session and body allocations.</summary>
internal sealed partial class ProbeApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _metadata = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly List<Measurement> _measurements = [];
    private readonly CanvasAnimatedControl _canvas = new();
    private readonly LyricsRenderCoordinator _lyrics = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _liveDone;
    private int _liveFrame;
    private int _liveKind;
    private long _previousExitBytes;
    private long _liveBoundaryBytes;
    private long _liveSessionBytes;
    private long _liveBodyBytes;
    private long _liveBodyTicks;
    private long _liveUpdateBytes;
    private long _updateSinceDraw;
    private Exception? _failure;
    private static readonly TimeSpan Delta = TimeSpan.FromSeconds(1.0 / 60);
    private const int LiveWarmup = 30;
    private const int LiveSamples = 120;

    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new Window { Content = _canvas, Title = "Draw allocation probe" };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        string resultPath = Path.Combine(AppContext.BaseDirectory, "result.json");
        FrameEventSubscription? update = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            _lyrics.Canvas = _canvas;
            _lyrics.LyricsRegion = new Rect(0, 0, 1100, 700);
            _lyrics.Attach();
            _canvas.CreateResources += (_, _) => { _lyrics.OnCreateResources(); _ready.TrySetResult(); };
            _canvas.Draw += OnLiveDraw;
            update = FrameEventSubscription.SubscribeUpdate(_canvas, OnLiveUpdate);
            window.Activate();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _canvas.Paused = true;
            if (Environment.GetCommandLineArgs().Contains("--lifetime"))
            {
                update.Dispose();
                update = null;
                _canvas.Draw -= OnLiveDraw;
                await RunLifetimeProbeAsync(resultPath);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--input"))
            {
                await RunInputProbeAsync(window, resultPath);
                return;
            }
            await _canvas.RunOnGameLoopThreadAsync(RunPrimitiveMeasurements);
            await _canvas.RunOnGameLoopThreadAsync(RunRendererMeasurements);
            await MeasureLiveAsync("live-empty", 0);
            await MeasureLiveAsync("live-lyrics", 1);
            _canvas.Draw -= OnLiveDraw;
            using var rawDraw = FrameEventSubscription.SubscribeDraw(_canvas, OnRawDraw);
            await MeasureLiveAsync("live-native-empty", 0);
            await MeasureLiveAsync("live-native-lyrics", 1);
            if (_failure is { } failure) throw failure;
            var report = new
            {
                Status = "PASS",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                RuntimeFileVersion = FileVersionInfo.GetVersionInfo(typeof(WinRT.ComWrappersSupport).Assembly.Location).FileVersion,
                Environment.ProcessorCount,
                Width = _canvas.Size.Width,
                Height = _canvas.Size.Height,
                Dpi = _canvas.Dpi,
                Note = "Exact current-thread managed bytes after warmup. CPU submission wall time excludes GPU completion; live boundary includes native return/event projection/loop work. No induced GC. Synthetic lyrics, production renderer, real D3D/DirectWrite resources.",
                PixelChecks = NativeDrawingChecks.Results,
                Measurements = _measurements
            };
            File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "FAIL", Error = error.ToString(), Measurements = _measurements }));
        }
        finally
        {
            _canvas.Paused = true;
            update?.Dispose();
            _canvas.Draw -= OnLiveDraw;
            _lyrics.PrepareForShutdown();
            _canvas.RemoveFromVisualTree();
            window.Close();
            Exit();
        }
    }

    private void RunPrimitiveMeasurements()
    {
        NativeDrawingChecks.Run(_canvas);
        CharacterCacheChecks.Run(_canvas);
        using var target = new CanvasRenderTarget(_canvas, 1100, 700);
        using var image = new CanvasCommandList(_canvas);
        using (var imageSession = image.CreateDrawingSession())
            imageSession.DrawText("Draw allocation probe 中文", 10, 10, Colors.White);
        Measure("primitive-render-target-session", () => { using var ds = target.CreateDrawingSession(); ds.Clear(Colors.Transparent); });
        Measure("primitive-command-list-and-session", () =>
        {
            using var list = new CanvasCommandList(_canvas);
            using var ds = list.CreateDrawingSession();
            ds.DrawImage(image);
        });
        using var session = target.CreateDrawingSession();
        Measure("primitive-cached-image", () => session.DrawImage(image));
        Measure("primitive-opacity-layer", () =>
        {
            using var layer = session.CreateLayer(0.5f, new Rect(0, 0, 1100, 700));
            session.DrawImage(image);
        });
        using var nativeTarget = new CanvasRenderTarget(_canvas, 1100, 700);
        Measure("native-render-target-session", () =>
        {
            using var scope = CanvasDrawingSessionScope.Create(nativeTarget);
            scope.Context.Clear(Colors.Transparent);
        });
        using var borrowed = CanvasDrawingSessionScope.Borrow(session);
        MeasureNative("native-opacity-layer", borrowed.Context, context =>
        {
            using var layer = context.CreateLayer(0.5f, new Rect(0, 0, 1100, 700));
            context.DrawImage(image);
        });
    }

    private void RunRendererMeasurements()
    {
        using var target = new CanvasRenderTarget(_canvas, 1100, 700);
        using var ds = target.CreateDrawingSession();
        foreach (BaseBackgroundRenderer renderer in new BaseBackgroundRenderer[]
        {
            new LiquidFlowBackgroundRenderer(), new RotatingMeshBackgroundRenderer()
        })
        {
            using (renderer)
            {
                renderer.LoadResources();
                renderer.Update(Delta);
                Measure("background-" + renderer.GetType().Name, () => renderer.Draw(_canvas, ds), 120, 12);
            }
        }
        ConfigureLyrics(false);
        WarmLyrics(ds);
        Measure("lyrics-update-stable", () => _lyrics.OnUpdate(_canvas, Delta));
        Measure("lyrics-draw-stable", () => _lyrics.OnDraw(_canvas, ds), 240, 30);
        ConfigureLyrics(true);
        WarmLyrics(ds);
        Measure("lyrics-update-effects", () => _lyrics.OnUpdate(_canvas, Delta));
        Measure("lyrics-draw-effects", () => _lyrics.OnDraw(_canvas, ds), 240, 30);
        int step = 0;
        Measure("lyrics-moving-update-and-draw", () =>
        {
            TimeProgressBus.Publish(15000 + step++ * 16 % 30000);
            _lyrics.OnUpdate(_canvas, Delta);
            _lyrics.OnDraw(_canvas, ds);
        }, 240, 30);
        Measure("lyrics-republish-update-and-draw", () =>
        {
            UILyricsBus.Publish(CreateLyrics());
            _lyrics.OnUpdate(_canvas, Delta);
            _lyrics.OnDraw(_canvas, ds);
        }, 12, 2);
    }

    private void WarmLyrics(CanvasDrawingSession ds)
    {
        TimeProgressBus.Publish(15500);
        for (int i = 0; i < 90; i++)
        {
            _lyrics.OnUpdate(_canvas, Delta);
            _lyrics.OnDraw(_canvas, ds);
        }
    }

    private void ConfigureLyrics(bool effects)
    {
        var settings = new LyricsSettingsBus.Settings("Segoe UI", CanvasHorizontalAlignment.Left, true, 1,
            effects ? 8 : 0, effects ? 6 : 0, effects ? 8 : 0, effects ? 1.1 : 0, 1200,
            true, true, 0.5, 0.7, 1, EasingType.Sine, EaseMode.Out, 0.35, 60, false, Colors.White);
        LyricsSettingsBus.Publish(settings);
        LyricsFontSizeBus.Publish(36);
        UILyricsBus.Publish(CreateLyrics());
        IsPlayingBus.Publish(false);
        TimeProgressBus.Publish(15500);
    }

    private static List<LyricLine> CreateLyrics()
    {
        var result = new List<LyricLine>(40);
        for (int i = 0; i < 40; i++)
        {
            var line = new LyricLine { StartMs = i * 3000, EndMs = (i + 1) * 3000,
                TransLateText = "真实原生歌词绘制分配测量", PronunciationText = "Native lyric rendering allocation probe" };
            for (int word = 0; word < 6; word++)
                line.Words.Add(new() { Word = "分配测量 ", StartMs = i * 3000 + word * 500, DurationMs = 500 });
            result.Add(line);
        }
        return result;
    }

    private void Measure(string name, Action operation, int iterations = 1000, int warmup = 50)
    {
        if (Environment.GetCommandLineArgs().Contains("--trace") && !name.Contains("republish")) iterations *= 6;
        for (int i = 0; i < warmup; i++) operation();
        ProbeEvents.Log.PhaseStart(name);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) operation();
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        ProbeEvents.Log.PhaseStop(name);
        _measurements.Add(new(name, iterations, allocated, elapsed * 1000000.0 / Stopwatch.Frequency));
    }

    private delegate void NativeOperation(CanvasDrawContext context);

    private void MeasureNative(string name, CanvasDrawContext context, NativeOperation operation)
    {
        for (int i = 0; i < 50; i++) operation(context);
        ProbeEvents.Log.PhaseStart(name);
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1000; i++) operation(context);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        ProbeEvents.Log.PhaseStop(name);
        _measurements.Add(new(name, 1000, allocated, elapsed * 1000000.0 / Stopwatch.Frequency));
    }

    private async Task MeasureLiveAsync(string name, int kind)
    {
        _canvas.Paused = true;
        await _canvas.RunOnGameLoopThreadAsync(() =>
        {
            if (kind == 1) ConfigureLyrics(true);
            _liveFrame = 0;
            _liveKind = kind;
            _previousExitBytes = 0;
            _liveBoundaryBytes = _liveSessionBytes = _liveBodyBytes = _liveBodyTicks = _liveUpdateBytes = _updateSinceDraw = 0;
            _liveDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        });
        ProbeEvents.Log.PhaseStart(name);
        _canvas.Paused = false;
        await _liveDone!.Task.WaitAsync(TimeSpan.FromSeconds(12));
        _canvas.Paused = true;
        await _canvas.RunOnGameLoopThreadAsync(() => { });
        ProbeEvents.Log.PhaseStop(name);
        _measurements.Add(new(name + "-boundary-and-loop", LiveSamples, _liveBoundaryBytes, 0));
        _measurements.Add(new(name + "-drawing-session", LiveSamples, _liveSessionBytes, 0));
        _measurements.Add(new(name + "-draw-body", LiveSamples, _liveBodyBytes, _liveBodyTicks * 1000000.0 / Stopwatch.Frequency));
        _measurements.Add(new(name + "-update-body", LiveSamples, _liveUpdateBytes, 0));
    }

    private void OnLiveUpdate(TimeSpan elapsed)
    {
        if (_liveDone is null || _liveDone.Task.IsCompleted || _liveKind == 0) return;
        long before = GC.GetAllocatedBytesForCurrentThread();
        _lyrics.OnUpdate(_canvas, elapsed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _updateSinceDraw += allocated;
        if (_liveFrame >= LiveWarmup) _liveUpdateBytes += allocated;
    }

    private void OnLiveDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_liveDone is null || _liveDone.Task.IsCompleted) return;
        try
        {
            long entry = GC.GetAllocatedBytesForCurrentThread();
            long boundary = _previousExitBytes == 0 ? 0 : entry - _previousExitBytes - _updateSinceDraw;
            _updateSinceDraw = 0;
            long start = Stopwatch.GetTimestamp();
            var ds = args.DrawingSession;
            long sessionEnd = GC.GetAllocatedBytesForCurrentThread();
            ds.Clear(Colors.Transparent);
            if (_liveKind == 1) _lyrics.OnDraw(sender, ds);
            long bodyEnd = GC.GetAllocatedBytesForCurrentThread();
            long elapsed = Stopwatch.GetTimestamp() - start;
            if (_liveFrame >= LiveWarmup && _liveFrame < LiveWarmup + LiveSamples)
            {
                _liveBoundaryBytes += boundary;
                _liveSessionBytes += sessionEnd - entry;
                _liveBodyBytes += bodyEnd - sessionEnd;
                _liveBodyTicks += elapsed;
            }
            _previousExitBytes = GC.GetAllocatedBytesForCurrentThread();
            if (++_liveFrame >= LiveWarmup + LiveSamples) _liveDone.TrySetResult();
        }
        catch (Exception error)
        {
            _failure = error;
            _liveDone.TrySetException(error);
        }
    }

    private void OnRawDraw(CanvasDrawContext context)
    {
        if (_liveDone is null || _liveDone.Task.IsCompleted) return;
        try
        {
            long entry = GC.GetAllocatedBytesForCurrentThread();
            long boundary = _previousExitBytes == 0 ? 0 : entry - _previousExitBytes - _updateSinceDraw;
            _updateSinceDraw = 0;
            long start = Stopwatch.GetTimestamp();
            context.Clear(Colors.Transparent);
            if (_liveKind == 1) _lyrics.OnDraw(_canvas, context);
            long bodyEnd = GC.GetAllocatedBytesForCurrentThread();
            long elapsed = Stopwatch.GetTimestamp() - start;
            if (_liveFrame >= LiveWarmup && _liveFrame < LiveWarmup + LiveSamples)
            {
                _liveBoundaryBytes += boundary;
                _liveBodyBytes += bodyEnd - entry;
                _liveBodyTicks += elapsed;
            }
            _previousExitBytes = GC.GetAllocatedBytesForCurrentThread();
            if (++_liveFrame >= LiveWarmup + LiveSamples) _liveDone.TrySetResult();
        }
        catch (Exception error)
        {
            _failure = error;
            _liveDone.TrySetException(error);
        }
    }
}

/// <summary>Stores exact thread allocations and measured submission time for one warmed phase.</summary>
internal sealed record Measurement(string Name, int Iterations, long AllocatedBytes, double ElapsedMicroseconds)
{
    public double BytesPerOperation => (double)AllocatedBytes / Iterations;
    public double MicrosecondsPerOperation => ElapsedMicroseconds / Iterations;
}
