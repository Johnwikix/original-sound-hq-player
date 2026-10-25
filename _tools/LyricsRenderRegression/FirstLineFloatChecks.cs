using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Replays a playback-clock correction after the first lyric frame has been drawn.</summary>
internal static class FirstLineFloatChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        var failures = new List<string>();
        foreach (var mode in new[] { EaseMode.FlowWave, EaseMode.Out })
        foreach (long start in new long[] { 0, 1000 })
        foreach (bool correctClock in new[] { false, true })
        foreach (bool switchSong in new[] { false, true })
        {
            string? failure = RunCase(canvas, mode, start, correctClock, switchSong);
            if (failure is not null) failures.Add(failure);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static string? RunCase(CanvasAnimatedControl canvas, EaseMode mode, long start, bool correctClock, bool switchSong)
    {
        long position = 0;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 0,
                0, 5, 0, 500, false, false, 0.3, 0.5, 0, EasingType.Sine, mode,
                0.33, 120, false, Microsoft.UI.Colors.White));
            var lyrics = new long[] { start, start + 4000 }.Select(lineStart => new LyricLine
            {
                StartMs = lineStart,
                EndMs = lineStart + 4000,
                Words = Enumerable.Range(0, 4).Select(index => new LyricWord
                {
                    Word = "ABCD"[index].ToString(),
                    StartMs = lineStart + index * 1000,
                    DurationMs = 1000
                }).ToList()
            }).ToList();
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            if (switchSong)
            {
                position = 10000;
                UILyricsBus.Publish([new LyricLine
                {
                    StartMs = 0, EndMs = 20000,
                    Words = [new LyricWord { Word = "Previous song", StartMs = 0, DurationMs = 20000 }]
                }]);
                using var drawing = target.CreateDrawingSession();
                coordinator.OnDraw(canvas, drawing);
                position = 0;
            }
            UILyricsBus.Publish(lyrics);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var update = Update.CreateDelegate<Action<ICanvasResourceCreator, TimeSpan>>(coordinator);
            void Step(long time)
            {
                position = time;
                update(canvas, TimeSpan.FromMilliseconds(8));
                using var drawing = target.CreateDrawingSession();
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                coordinator.OnDraw(canvas, drawing);
            }
            long firstStep = correctClock ? start + 600 : 8;
            for (long time = firstStep; time <= start + 1200; time += 8) Step(time);
            var pending = lines[0].PrimaryRenderChars[2];
            string scenario = $"mode={mode}, start={start}, correctClock={correctClock}, switchSong={switchSong}";
            if (pending.FloatTransition.Value < 4.9)
                return $"First-line pending character lost its float after clock synchronization: {scenario}, float={pending.FloatTransition.Value:F3}, target={pending.FloatTransition.TargetValue:F3}.";

            if (mode == EaseMode.FlowWave)
            {
                for (long time = start + 2400; time <= start + 2904; time += 8) Step(time);
                if (lines[0].PrimaryRenderChars[3].FloatTransition.Value < 4.9
                    || lines[0].PrimaryRenderChars.Take(2).Any(character => character.FloatTransition.Value != 0))
                    return $"Forward correction must restore pending floats and leave completed characters settled: {scenario}.";
                for (long time = start + 400; time <= start + 912; time += 8) Step(time);
                if (lines[0].PrimaryRenderChars.Skip(1).Any(character => character.FloatTransition.Value < 4.9))
                    return $"Backward correction within the same first line must restore its pending floats: {scenario}.";

                IsPlayingBus.Publish(false);
                for (int frame = 0; frame < 60; frame++) Step(position);
                if (lines[0].PrimaryRenderChars.Skip(1).Any(character => character.FloatTransition.Value < 4.9))
                    return $"Pausing after a clock correction must retain pending floats: {scenario}.";
            }
            return null;
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
}
