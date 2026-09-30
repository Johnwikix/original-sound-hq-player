using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Replays a long final syllable at a delayed flow-wave row entrance.</summary>
internal static class FlowWaveWordExitChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Focus = typeof(LyricsRenderCoordinator).GetField("_currentLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Timing = typeof(LyricsRenderCoordinator).GetField("_lineScrollTiming", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        RunCase(canvas, 0);
        RunCase(canvas, 300);
        RunCase(canvas, 1500);
    }

    private static void RunCase(CanvasAnimatedControl canvas, int wordEndMargin)
    {
        long position = 0;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 5,
                5, 5, 110, 500, true, true, 0.3, 0.5, 0, EasingType.FlowWave, EaseMode.FlowWave,
                0.33, 120, false, Microsoft.UI.Colors.White));
            long[] starts = [0, 1500, 3000, 5000, 7000, 9000];
            UILyricsBus.Publish(starts.Select((start, index) => new LyricLine
            {
                StartMs = start,
                EndMs = index + 1 < starts.Length ? starts[index + 1] : 11000,
                Words = [new LyricWord { Word = "A", StartMs = start,
                    DurationMs = Math.Max(100, (index + 1 < starts.Length ? starts[index + 1] : 11000) - start - wordEndMargin) }]
            }).ToList());
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            RenderLyricsChar? trackedCharacter = null;
            void Step(long time, double elapsedMs = 8)
            {
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromMilliseconds(elapsedMs)]);
                if (trackedCharacter is not null) trackedCharacter.Crop.SourceRectangle = new Rect();
                using var drawing = target.CreateDrawingSession();
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                coordinator.OnDraw(canvas, drawing);
            }
            for (long time = 8; time <= 4984; time += 8) Step(time);
            var outgoing = lines[2];
            var character = outgoing.PrimaryRenderChars.Single();
            trackedCharacter = character;
            double glow = character.GlowTransition.Value;
            double opacity = outgoing.PlayedPrimaryOpacityTransition.Value;
            double offset = outgoing.ScrollMotion.Value;
            if (wordEndMargin > 300)
            {
                if (glow > 0.001 || Math.Abs(character.FloatTransition.Value) > 0.001)
                    throw new InvalidOperationException("A real long inter-line gap must let word effects settle, rather than keep glowing until the next row.");
                Step(5000, 16);
                if (outgoing.IsWordEffectsRetiring)
                    throw new InvalidOperationException("An already settled word must not restart a flow-wave visual tail.");
                return;
            }
            if (glow < 4 || opacity < 0.99)
                throw new InvalidOperationException($"The flow-wave reproduction must reach visible glow: glow={glow}, opacity={opacity}.");
            Step(5000, 16);
            var timing = (LyricScrollTiming)Timing.GetValue(coordinator)!;
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "wave-word-exit-trace.txt"),
                $"word margin={wordEndMargin}ms; outgoing delay={timing.GetLineTiming(2).Delay:F6}s; glow={glow:F6}->{character.GlowTransition.Value:F6}; " +
                $"opacity={opacity:F6}->{outgoing.PlayedPrimaryOpacityTransition.Value:F6}; " +
                $"offset={offset:F6}->{outgoing.ScrollMotion.Value:F6}; focus={Focus.GetValue(coordinator)}\n");
            if ((int)Focus.GetValue(coordinator)! != 3 || outgoing.IsPlayingLastFrame)
                throw new InvalidOperationException("Musical focus must switch at 5000ms without extending the word time.");
            if (timing.GetLineTiming(2).Delay <= 0)
                throw new InvalidOperationException("The reproduction must include a delayed outgoing flow-wave row.");
            if (outgoing.PlayedPrimaryOpacityTransition.Value == opacity && outgoing.ScrollMotion.Value == offset
                && character.GlowTransition.Value < glow * 0.8)
                throw new InvalidOperationException("The long final syllable snaps out of glow while the outgoing row still waits, fully highlighted and stationary.");
            if (character.GlowTransition.Value > 1 && character.Crop.SourceRectangle.Width <= 0)
                throw new InvalidOperationException("The native renderer discards the outgoing word-effect path while its glow is still visible.");
            if (character.EndMs != 5000 - wordEndMargin || character.GetPlayProgress(position) != 1)
                throw new InvalidOperationException("Visual retirement must not stretch word timing or fill progress.");
            for (long time = 5008; time <= 5080; time += 8) Step(time);
            if (!outgoing.IsWordEffectsRetiring || character.GlowTransition.Value != glow
                || outgoing.ScrollMotion.Value != offset || character.Crop.SourceRectangle.Width <= 0)
                throw new InvalidOperationException("The word effect must hold and remain drawn during the shared wave delay.");
            for (long time = 5088; time <= 5200; time += 8) Step(time);
            if (!outgoing.IsWordEffectsRetiring || character.GlowTransition.Value <= 0
                || character.GlowTransition.Value >= glow || character.FloatTransition.Value <= 0
                || outgoing.ScrollMotion.Value >= offset || outgoing.BlurAmountTransition.Value <= 0
                || Math.Abs(character.Glow.BlurAmount - outgoing.BlurAmountTransition.Value) > 0.001)
                throw new InvalidOperationException("Word return, real per-character blur and row displacement must progress together.");
            IsPlayingBus.Publish(false);
            Step(5200);
            if (!outgoing.IsWordEffectsRetiring || outgoing.IsPlayingLastFrame || character.Crop.SourceRectangle.Width <= 0)
                throw new InvalidOperationException("Pausing an outgoing row must keep its visual tail without reactivating musical highlight.");
            IsPlayingBus.Publish(true);
            for (long time = 5208; time <= 5720; time += 8) Step(time);
            if (outgoing.IsWordEffectsRetiring || character.GlowTransition.Value != 0
                || character.ScaleTransition.Value != 1 || character.FloatTransition.Value != 0)
                throw new InvalidOperationException("The outgoing word must settle and release its visual retirement state.");

            Step(4200);
            if ((int)Focus.GetValue(coordinator)! != 2 || outgoing.IsWordEffectsRetiring
                || character.GetPlayProgress(position) >= 1 || character.GlowTransition.Value > 0.1)
                throw new InvalidOperationException("Backward seek must clear the old tail and restore partial timed-word progress.");
            for (long time = 4208; time <= 5000; time += 8) Step(time);
            if (!outgoing.IsWordEffectsRetiring || character.Crop.SourceRectangle.Width <= 0)
                throw new InvalidOperationException("A replayed entrance must start one new word exit without retaining old delay configuration.");
            Step(8000);
            if (lines.Any(line => line.IsWordEffectsRetiring) || character.GlowTransition.Value != 0
                || character.ScaleTransition.Value != 1 || character.FloatTransition.Value != 0)
                throw new InvalidOperationException("Forward seek must discard all old visual tails.");
            coordinator.InvalidateStyle();
            Step(8000, 0);
            if (lines.Any(line => line.IsWordEffectsRetiring || line.ScrollMotion.IsMoving))
                throw new InvalidOperationException("Relayout must not retain delayed word effects or displacement.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    internal static void RunSong(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        long position = 153255;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 5,
                5, 5, 110, 500, true, true, 0.3, 0.5, 0, EasingType.FlowWave, EaseMode.FlowWave,
                0.33, 120, false, Microsoft.UI.Colors.White));
            UILyricsBus.Publish(lyrics);
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var outgoing = lines.Single(line => line.StartMs == 153255);
            var brave = outgoing.PrimaryRenderSyllables.Single(word => word.Text == "brave");
            void Step(long time, double elapsedMs = 8)
            {
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromMilliseconds(elapsedMs)]);
                using var drawing = target.CreateDrawingSession();
                coordinator.OnDraw(canvas, drawing);
            }
            for (long time = 153263; time <= 157673; time += 8) Step(time);
            Step(157680, 9);
            if (Math.Abs(brave.EndMs!.Value - 157381) > 0.001
                || brave.ChildrenRenderLyricsChars.Any(character => character.GetPlayProgress(position) != 1
                    || character.GlowTransition.Value < 4.99 || character.FloatTransition.Value < 4.99))
                throw new InvalidOperationException("Actual brave must preserve its 300ms word timing while retaining the final word's flow-wave visual emphasis.");
            Step(157681, 1);
            if (!outgoing.IsWordEffectsRetiring || outgoing.IsPlayingLastFrame
                || (int)Focus.GetValue(coordinator)! != lines.IndexOf(outgoing) + 1)
                throw new InvalidOperationException("Actual brave must enter visual retirement while musical focus switches exactly at 157681ms.");
            for (long time = 157689; time <= 158681; time += 8) Step(time);
            if (outgoing.IsWordEffectsRetiring || brave.ChildrenRenderLyricsChars.Any(character =>
                character.GlowTransition.Value != 0 || character.ScaleTransition.Value != 1 || character.FloatTransition.Value != 0))
                throw new InvalidOperationException("Actual brave's effects must finish instead of lingering across the following lyric.");
            Step(156800);
            if (outgoing.IsWordEffectsRetiring || !outgoing.IsPlayingLastFrame
                || brave.ChildrenRenderLyricsChars.All(character => character.GetPlayProgress(position) == 1))
                throw new InvalidOperationException("Actual brave's partial word progress must recover on backward seek.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
}
