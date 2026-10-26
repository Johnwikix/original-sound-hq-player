using System.Diagnostics;
using System.Text.Json;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

/// <summary>Measures production pointer handlers using real Windows-routed input.</summary>
internal sealed partial class ProbeApplication
{
    private long _moveBytes, _wheelBytes, _moveTicks, _wheelTicks;
    private int _moves, _wheels;
    private bool _replayedMove, _replayedWheel;

    private async Task RunInputProbeAsync(Window window, string resultPath)
    {
        bool interactive = Environment.GetCommandLineArgs().Contains("--interactive-input");
        window.Title = "Pointer allocation probe";
        if (interactive)
        {
            window.AppWindow.Move(new(100, 100));
            window.AppWindow.Show();
            window.Activate();
        }
        await Task.Delay(150);
        _canvas.Paused = true;
        await _canvas.RunOnGameLoopThreadAsync(() =>
        {
            ConfigureLyrics(true);
            for (int i = 0; i < 90; i++) _lyrics.OnUpdate(_canvas, Delta);
            if (!_lyrics.HasLyrics) throw new InvalidOperationException("Pointer probe has no production lyric rows.");
        });
        using var draw = FrameEventSubscription.SubscribeDraw(_canvas, context =>
        {
            context.Clear(Colors.DarkSlateGray);
            _lyrics.OnDraw(_canvas, context);
        });
        _canvas.PointerMoved += MeasureMove;
        _canvas.PointerWheelChanged += MeasureWheel;
        _canvas.Invalidate();
        ProbeEvents.Log.PhaseStart("pointer-capture-real-events");
        string injectionMode;
        try { injectionMode = await NativePointerDriver.RunAsync(window, () => _moves, () => _wheels, interactive); }
        finally
        {
            ProbeEvents.Log.PhaseStop("pointer-capture-real-events");
            _canvas.PointerMoved -= MeasureMove;
            _canvas.PointerWheelChanged -= MeasureWheel;
        }
        if (_moves <= 5 || _wheels <= 5) throw new InvalidOperationException($"Insufficient real pointer input: {_moves} moves, {_wheels} wheel events.");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            Status = "PASS", InjectionMode = injectionMode, AllDeliveredValuesMatchProjection = true, Note = "Real routed input from the isolated window; callback-body thread allocations exclude the framework's event-args creation. Getter replay reuses a captured native event during its original callback; it is not an input-rate benchmark.",
            Moves = _moves - 5, MoveBytes = _moveBytes, MoveBytesPerEvent = (double)_moveBytes / (_moves - 5),
            MoveMicrosecondsPerEvent = _moveTicks * 1000000.0 / Stopwatch.Frequency / (_moves - 5),
            Wheels = _wheels - 5, WheelBytes = _wheelBytes, WheelBytesPerEvent = (double)_wheelBytes / (_wheels - 5),
            WheelMicrosecondsPerEvent = _wheelTicks * 1000000.0 / Stopwatch.Frequency / (_wheels - 5),
            Measurements = _measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ValidatePointer(PointerRoutedEventArgs args, bool wheel)
    {
        using var native = PointerPointScope.GetCurrentPoint(args, _canvas);
        var projected = args.GetCurrentPoint(_canvas);
        if (native.Position.X != projected.Position.X || native.Position.Y != projected.Position.Y
            || (wheel && native.MouseWheelDelta != projected.Properties.MouseWheelDelta))
            throw new InvalidOperationException("Native pointer values differ from the projection.");
    }
    private void MeasureMove(object sender, PointerRoutedEventArgs args)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        _lyrics.OnPointerMoved(sender, args);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (++_moves > 5) { _moveBytes += allocated; _moveTicks += elapsed; }
        ValidatePointer(args, false);
        if (!_replayedMove)
        {
            _replayedMove = true;
            using var native = PointerPointScope.GetCurrentPoint(args, _canvas);
            var projected = args.GetCurrentPoint(_canvas).Position;
            if (native.Position.X != projected.X || native.Position.Y != projected.Y)
                throw new InvalidOperationException("Native pointer position differs from WinUI projection.");
            Measure("pointer-replay-position-getter", () => { _ = args.GetCurrentPoint(_canvas).Position; });
            Measure("pointer-native-position-getter", () =>
            {
                using var point = PointerPointScope.GetCurrentPoint(args, _canvas);
                _ = point.Position;
            });
        }
    }

    private void MeasureWheel(object sender, PointerRoutedEventArgs args)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        _lyrics.OnPointerWheelChanged(sender, args);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (++_wheels > 5) { _wheelBytes += allocated; _wheelTicks += elapsed; }
        ValidatePointer(args, true);
        if (!_replayedWheel)
        {
            _replayedWheel = true;
            using var native = PointerPointScope.GetCurrentPoint(args, _canvas);
            var projected = args.GetCurrentPoint(_canvas);
            if (native.Position.X != projected.Position.X || native.Position.Y != projected.Position.Y
                || native.MouseWheelDelta != projected.Properties.MouseWheelDelta)
                throw new InvalidOperationException("Native wheel snapshot differs from WinUI projection.");
            Measure("pointer-replay-double-point-and-wheel", () =>
            {
                _ = args.GetCurrentPoint(_canvas).Position;
                _ = args.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
            });
            Measure("pointer-native-position-and-wheel", () =>
            {
                using var point = PointerPointScope.GetCurrentPoint(args, _canvas);
                _ = point.Position;
                _ = point.MouseWheelDelta;
            });
        }
    }
}
