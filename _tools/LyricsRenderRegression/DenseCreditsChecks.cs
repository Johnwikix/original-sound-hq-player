using System.Diagnostics;
using System.Reflection;
using System.Text;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

internal static class DenseCreditsChecks
{
    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        var updateMethod = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var linesField = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var timingField = typeof(LyricsRenderCoordinator).GetField("_lineScrollTiming", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var report = new StringBuilder();
        foreach (var mode in new[] { EaseMode.FlowWave, EaseMode.Out })
        {
            long position = 0;
            TimeProgressBus.SetClock(() => position);
            var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
            try
            {
                coordinator.Attach();
                coordinator.OnCreateResources();
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 5,
                    5, 5, 110, 500, true, true, 0.3, 0.5, 0, EasingType.FlowWave, mode,
                    0.4, 120, false, Microsoft.UI.Colors.White));
                UILyricsBus.Publish(lyrics);
                IsPlayingBus.Publish(true);
                using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
                using (var draw = target.CreateDrawingSession()) coordinator.OnDraw(canvas, draw);
                var lines = (List<RenderLyricsLine>)linesField.GetValue(coordinator)!;
                var chorus = lines.Single(line => line.StartMs == 1421);
                var update = updateMethod.CreateDelegate<Action<ICanvasResourceCreator, TimeSpan>>(coordinator);
                report.AppendLine($"mode={mode}; lines={lines.Count}; chorusChars={chorus.PrimaryRenderChars.Count}");
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                for (position = 8; position <= 2200; position += 8)
                {
                    double before = chorus.ScrollMotion.Value;
                    long tick = Stopwatch.GetTimestamp();
                    update(canvas, TimeSpan.FromMilliseconds(8));
                    double updateMs = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                    tick = Stopwatch.GetTimestamp();
                    using (var draw = target.CreateDrawingSession()) coordinator.OnDraw(canvas, draw);
                    double drawMs = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                    if (position >= 1360 && position <= 1904)
                    {
                        var timing = (LyricScrollTiming)timingField.GetValue(coordinator)!;
                        report.AppendLine($"{position}: move={chorus.ScrollMotion.Value-before:F3}, velocity={chorus.ScrollMotion.Velocity:F1}, delay={timing.GetLineTiming(lines.IndexOf(chorus)).Delay:F3}, update={updateMs:F3}ms, draw={drawMs:F3}ms");
                    }
                }
                report.AppendLine($"GC collections={GC.CollectionCount(0)-gc0}/{GC.CollectionCount(1)-gc1}/{GC.CollectionCount(2)-gc2}");
            }
            finally { coordinator.PrepareForShutdown(); TimeProgressBus.SetClock(null); }
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dense-credits.txt"), report.ToString());
    }
}
