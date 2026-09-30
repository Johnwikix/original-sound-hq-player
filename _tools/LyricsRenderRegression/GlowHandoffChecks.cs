using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Measures the final glow-to-inactive handoff on real Paint It Black glyph pixels.</summary>
internal static class GlowHandoffChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        if (lyrics.Count != 44) throw new InvalidOperationException("The complete Paint It Black fixture must project 44 real rows and ignore its empty final row.");
        string traceFile = Path.Combine(AppContext.BaseDirectory, "glow-handoff-trace.txt");
        File.WriteAllText(traceFile, "");
        foreach (var (start, cut, frameMs, isDark) in new[]
        {
            (16457L, 20681L, 4, false), (22877L, 26717L, 8, true), (41417L, 44948L, 4, true),
            (70870L, 75047L, 8, false), (83323L, 87175L, 4, false), (120015L, 123927L, 8, true)
        }) RunCase(canvas, lyrics, start, cut, frameMs, isDark, traceFile);
    }

    private static void RunCase(CanvasAnimatedControl canvas, List<LyricLine> lyrics, long rowStart,
        long cut, int frameMs, bool isDark, string traceFile)
    {
        long position = rowStart == 16457 ? 14923 : rowStart;
        long begin = position;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, isDark, 1, 5,
                5, 5, 110, 500, true, true, 0.3, 0.5, 0, EasingType.FlowWave, EaseMode.FlowWave,
                0.33, 120, false, Microsoft.UI.Colors.White));
            UILyricsBus.Publish(lyrics);
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var outgoing = lines.Single(line => line.StartMs == rowStart);
            var black = outgoing.PrimaryRenderSyllables.Single(word => word.Text == "black");
            var suffix = black.ChildrenRenderLyricsChars[^1];
            var prefix = outgoing.PrimaryRenderChars[0];
            void Step(long time)
            {
                double elapsedMs = time - position;
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromMilliseconds(elapsedMs)]);
                using var drawing = target.CreateDrawingSession();
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                coordinator.OnDraw(canvas, drawing);
            }
            for (long time = begin + frameMs; time < cut; time += frameMs) Step(time);
            Step(cut);
            if (!outgoing.IsWordEffectsRetiring || outgoing.IsPlayingLastFrame || suffix.GlowTransition.Value < 4.9)
                throw new InvalidOperationException("Actual black must reach its outgoing glow with musical focus already on the next row.");
            double beforeSuffix = 0, beforePrefix = 0, previousGlow = 0;
            double afterSuffix = 0, afterPrefix = 0;
            bool observedHandoff = false;
            using var rowTarget = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            var renderer = new LyricsLineRenderer
            {
                Line = outgoing,
                PlayedFillColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black,
                UnplayedFillColor = isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black,
                IsGlowEnabled = true, IsFloatEnabled = true, IsScaleEnabled = true
            };
            for (long time = cut + frameMs; time <= cut + 1000; time += frameMs)
            {
                bool wasRetiring = outgoing.IsWordEffectsRetiring;
                Step(time);
                // 最后一小段才读取 GPU 像素，避免为整个退场复制画面；仍逐帧运行真实协调器。
                if (outgoing.IsWordEffectsRetiring && suffix.GlowTransition.Value > 0.02) continue;
                renderer.CurrentProgressMs = position;
                renderer.IsPlaying = outgoing.GetIsHighlighted(position) || outgoing.IsWordEffectsRetiring;
                using (var drawing = rowTarget.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    drawing.Transform = Matrix3x2.CreateTranslation((float)(10 - outgoing.PrimaryPosition.X), (float)(10 - outgoing.PrimaryPosition.Y));
                    renderer.Draw(canvas, drawing);
                }
                var pixels = rowTarget.GetPixelColors();
                double suffixAlpha = Alpha(pixels, suffix.LayoutRect), prefixAlpha = Alpha(pixels, prefix.LayoutRect);
                if (wasRetiring && !outgoing.IsWordEffectsRetiring)
                {
                    afterSuffix = suffixAlpha;
                    afterPrefix = prefixAlpha;
                    observedHandoff = true;
                    break;
                }
                beforeSuffix = suffixAlpha;
                beforePrefix = prefixAlpha;
                previousGlow = suffix.GlowTransition.Value;
            }
            File.AppendAllText(traceFile,
                $"Paint It Black row={rowStart}ms; dark={isDark}; sample={frameMs}ms; time={position}ms; final glow={previousGlow:F8}->0; " +
                $"last glyph alpha={beforeSuffix:F2}->{afterSuffix:F2}; earlier glyph alpha={beforePrefix:F2}->{afterPrefix:F2}; " +
                $"played opacity={outgoing.PlayedPrimaryOpacityTransition.Value:F6}; inactive opacity={outgoing.UnplayedPrimaryOpacityTransition.Value:F6}\n");
            if (!observedHandoff || beforeSuffix <= 0 || beforePrefix <= 0)
                throw new InvalidOperationException("The native handoff measurement must capture both real glyphs on consecutive frames.");
            double suffixChange = Math.Abs(afterSuffix / beforeSuffix - 1);
            double prefixChange = Math.Abs(afterPrefix / beforePrefix - 1);
            if (suffixChange > 0.03 || prefixChange > 0.03)
                throw new InvalidOperationException($"Paint It Black's final glyph jumps at the glow handoff: last={suffixChange:P2}, earlier={prefixChange:P2}.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static double Alpha(Windows.UI.Color[] pixels, Rect glyph)
    {
        int left = Math.Clamp((int)Math.Floor(glyph.X + 10), 0, 599);
        int right = Math.Clamp((int)Math.Ceiling(glyph.X + glyph.Width + 10), left + 1, 600);
        int top = Math.Clamp((int)Math.Floor(glyph.Y + 10), 0, 399);
        int bottom = Math.Clamp((int)Math.Ceiling(glyph.Y + glyph.Height + 10), top + 1, 400);
        double alpha = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++) alpha += pixels[y * 600 + x].A;
        return alpha;
    }
}
