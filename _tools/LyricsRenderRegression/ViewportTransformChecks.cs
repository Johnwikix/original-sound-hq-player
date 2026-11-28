using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

internal static class ViewportTransformChecks
{
    private const int TargetWidth = 640;
    private const int TargetHeight = 440;
    private static readonly Rect Pane = new(75, 40, 450, 320);
    private static readonly Rect Viewport = new(20, 40, 600, 320);
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", Members)!;
    private static readonly MethodInfo HitTest = typeof(LyricsRenderCoordinator).GetMethod("FindHoveredLyricsLine", Members)!;
    private static readonly MethodInfo SetAutomaticPointer = typeof(LyricsRenderCoordinator).GetMethod("SetLyricsParallaxPointer", Members)!;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        VerifyPlaybackProjectionAcrossHostOrigins();
        VerifyViewport(canvas);
        VerifyRightPaneOverflow(canvas);
        VerifyAutomaticPointerOverflow(canvas);
        VerifyFanFocus(canvas, EasingType.FlowWave, EaseMode.FlowWave);
        VerifyFanFocus(canvas, EasingType.Sine, EaseMode.Out);
        VerifyFanFocus(canvas, EasingType.Linear, EaseMode.Continuous);
    }

    private static void VerifyPlaybackProjectionAcrossHostOrigins()
    {
        var pivot = new Vector2(412, 301);
        var pointOnPage = new Vector2(580, 360);
        var sceneOrigin = new Vector2(7, 32);
        var lyricsOrigin = new Vector2(354, 72);
        var angles = new Vector3(17, -13, 9);
        var pageProjection = PlaybackSpatialProjection.CreatePerspective(pivot, angles, 650);
        var projectedPoint = Project(pointOnPage, pageProjection);

        Check(Vector2.Distance(Project(pivot, pageProjection), pivot) < 0.001f,
            "The common playback pivot must remain fixed after rotating.");
        Check(Vector2.Distance(projectedPoint, pointOnPage) > 1,
            "The fixture must measurably project the point.");
        foreach (var origin in new[] { sceneOrigin, lyricsOrigin })
        {
            var localProjection = PlaybackSpatialProjection.ToLocal(pageProjection, origin);
            var projectedLocalPoint = Project(pointOnPage - origin, localProjection);
            Check(Vector2.Distance(projectedLocalPoint + origin, projectedPoint) < 0.001f,
                "Displaced playback hosts must project the same page point to the same screen point.");
        }

        var halfExtents = new Vector2(2450, 1500);
        var shallowProjection = PlaybackSpatialProjection.CreatePerspective(pivot, new Vector3(85, -72, 9), 100, halfExtents);
        foreach (float x in new[] { -halfExtents.X, halfExtents.X })
        {
            foreach (float y in new[] { -halfExtents.Y, halfExtents.Y })
            {
                var corner = pivot + new Vector2(x, y);
                var homogeneous = Vector4.Transform(new Vector4(corner, 0, 1), shallowProjection);
                Check(homogeneous.W >= 0.4999f, "Even maximum tilt and minimum depth must keep the foreground in front of the camera.");
            }
        }

        var neutral = PlaybackSpatialProjection.CreatePerspective(pivot, Vector3.Zero, 650);
        Check(PlaybackSpatialProjection.ToLocal(neutral, lyricsOrigin).IsIdentity,
            "A neutral playback pose must stay neutral in each host.");

        static Vector2 Project(Vector2 point, Matrix4x4 projection)
        {
            var homogeneous = Vector4.Transform(new Vector4(point, 0, 1), projection);
            return new Vector2(homogeneous.X, homogeneous.Y) / homogeneous.W;
        }
    }

    private static void VerifyViewport(CanvasAnimatedControl canvas)
    {
        long position = 3000;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = Pane, LyricsViewport = Viewport };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsFontSizeBus.Publish(46);
            UILyricsBus.Publish(Enumerable.Range(0, 9).Select(i => new LyricLine
            {
                StartMs = i * 1000,
                EndMs = (i + 1) * 1000,
                Words = [new() { Word = "□□□□□□  " + i, StartMs = i * 1000, DurationMs = 1000 }],
            }).ToList());
            IsPlayingBus.Publish(true);

            using var target = new CanvasRenderTarget(canvas.Device, TargetWidth, TargetHeight, 96);
            Color[] Draw(double anchor, int fan = 0, bool feather = false, bool perspective = false, bool automatic = false)
            {
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Center, true, 1, 0,
                    0, 0, 0, 500, false, false, 1, 1, 0, EasingType.Linear, EaseMode.InOut,
                    anchor, 60, false, Colors.White, showTranslation: false, showPronunciation: false,
                    isLyricsEdgeFeatheringEffectEnabled: feather, isFanLyricsEnabled: fan != 0, fanLyricsAngle: fan,
                    is3DLyricsEnabled: perspective, isAuto3DLyricsEnabled: automatic, lyrics3DXAngle: 18,
                    lyrics3DYAngle: -15, lyrics3DZAngle: 12, lyrics3DDepth: 600));
                for (int i = 0; i < 45; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
                using (var scope = CanvasDrawingSessionScope.Create(target))
                {
                    scope.Context.Clear(Colors.Transparent);
                    coordinator.OnDraw(canvas, scope.Context);
                }

                var pixels = target.GetPixelColors();
                Check(pixels.Any(color => color.A > 0), "The Win2D frame must contain clear lyric pixels.");
                for (int index = 0; index < pixels.Length; index++)
                {
                    if (pixels[index].A == 0) continue;
                    int x = index % TargetWidth;
                    int y = index / TargetWidth;
                    Check(x >= Viewport.Left && x < Viewport.Right && y >= Viewport.Top && y < Viewport.Bottom,
                        "Lyric pixels escaped the hard clip.");
                }

                return pixels;
            }

            const double anchor = 0.5;
            var baseline = Draw(anchor);
            var fanPositive = Draw(anchor, fan: 12);
            Check(!baseline.SequenceEqual(fanPositive), "A positive fan angle must change the output.");
            CheckFanHit(12);
            var fanNegative = Draw(anchor, fan: -12);
            Check(!fanPositive.SequenceEqual(fanNegative), "Reversing the fan must change its output.");
            CheckFanHit(-12);

            var userScrolling = typeof(LyricsRenderCoordinator).GetField("_userScrolling", Members)!;
            var cooldown = typeof(LyricsRenderCoordinator).GetField("_userScrollCooldownSec", Members)!;
            userScrolling.SetValue(coordinator, true);
            cooldown.SetValue(coordinator, 10d);
            for (int i = 0; i < 60; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
            var angleProperty = typeof(RenderLyricsLine).GetProperty("FanAngleTransition", Members)!;
            var lyricRows = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var angle = (ValueTransition<double>)angleProperty.GetValue(lyricRows[4])!;
            Check(Math.Abs(angle.Value) < 0.001,
                "The fan must flatten when manual scrolling takes control.");
            userScrolling.SetValue(coordinator, false);
            for (int i = 0; i < 60; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
            Check(Math.Abs(angle.Value) > 0.01, "The fan must restore its angle when automatic scrolling resumes.");

            var perspective = Draw(anchor, perspective: true);
            Check(!baseline.SequenceEqual(perspective), "Manual 3D must project the entire Win2D layer.");
            Draw(anchor, fan: 12, perspective: true);
            CheckFanHit(12, withPerspective: true);

            var neutral = Draw(anchor, perspective: true, automatic: true);
            SetAutomaticPointer.Invoke(coordinator, [new Vector2(-0.9f, 0.7f)]);
            var tilted = Draw(anchor, perspective: true, automatic: true);
            Check(!neutral.SequenceEqual(tilted), "An off-center pointer must tilt the lyric plane.");
            SetAutomaticPointer.Invoke(coordinator, [null]);
            for (int i = 0; i < 60; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
            Check(Draw(anchor, perspective: true, automatic: true).SequenceEqual(neutral),
                "The automatic lyric plane must return to the neutral pose after the pointer exits.");

            foreach (double edgeAnchor in new[] { 0.035, 0.965 })
            {
                var full = Draw(edgeAnchor);
                var feather = Draw(edgeAnchor, feather: true);
                int edgeStart = edgeAnchor < 0.5 ? (int)Pane.Top : (int)Pane.Bottom - 16;
                int edgeEnd = edgeStart + 16;
                int changed = 0;
                for (int y = edgeStart; y < edgeEnd; y++)
                {
                    for (int x = (int)Pane.Left; x < Pane.Right; x++)
                    {
                        int index = y * TargetWidth + x;
                        if (full[index].A > feather[index].A) changed++;
                    }
                }
                Check(changed > 0, "The feather must dim the selected visible edge.");
            }

            void CheckFanHit(int fan, bool withPerspective = false)
            {
                var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
                var playing = lines[3];
                foreach (int index in new[] { 2, 4 })
                {
                    var line = lines[index];
                    double distance = line.TopLeftPosition.Y - playing.TopLeftPosition.Y;
                    double angle = Math.Clamp(distance / (Pane.Height / 2), -1, 1) * fan * Math.PI / 180;
                    double hinge = fan < 0 ? Pane.Width * 1.5 : -Pane.Width * 0.5;
                    double shift = Math.Abs(angle) / (Math.PI / 2) * (Pane.Width / 2) * (fan < 0 ? 1 : -1);
                    double x = Pane.X + hinge + (line.CenterPosition.X - hinge) * Math.Cos(angle) + shift;
                    double y = Pane.Y + Pane.Height / 2 - playing.CenterPosition.Y + line.CenterPosition.Y
                        + (line.CenterPosition.X - hinge) * Math.Sin(angle);
                    if (x < Pane.Left || x >= Pane.Right || y < Pane.Top || y >= Pane.Bottom) continue;
                    if (withPerspective)
                    {
                        var pivot = new Vector2((float)(Pane.X + Pane.Width / 2), (float)(Pane.Y + Pane.Height / 2));
                        const float rad = MathF.PI / 180;
                        var rotation = Matrix4x4.CreateRotationX(18 * rad)
                            * Matrix4x4.CreateRotationY(-15 * rad) * Matrix4x4.CreateRotationZ(12 * rad);
                        var rotated = Vector3.Transform(new Vector3((float)x - pivot.X, (float)y - pivot.Y, 0), rotation);
                        double w = 1 + rotated.Z / 600;
                        x = pivot.X + rotated.X / w;
                        y = pivot.Y + rotated.Y / w;
                    }
                    if (x < Pane.Left || x >= Pane.Right || y < Pane.Top || y >= Pane.Bottom) continue;
                    int hit = (int)HitTest.Invoke(coordinator,
                        [new Point(x, y), (double)-playing.CenterPosition.Y, Pane.Height, anchor])!;
                    Check(hit == index, $"Projected fan line {index} failed hit test at {fan} degrees. Found {hit}.");
                    return;
                }

                throw new InvalidOperationException($"Neither reference row landed in the pane at {fan} degrees.");
            }
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static void VerifyRightPaneOverflow(CanvasAnimatedControl canvas)
    {
        var pane = new Rect(320, 25, 290, 390);
        var surface = new Rect(0, pane.Top, TargetWidth, pane.Height);
        const double anchor = 0.45;
        long position = 10500;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = pane, LyricsViewport = surface };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsFontSizeBus.Publish(28);
            var timings = new[] { 3000, 5000, 10000, 14000, 20000, 25000 };
            var source = new[] { "Top verse", "Upward verse", "Anchor verse", "Downward verse", "Base verse" }
                .Select((text, index) =>
                {
                    var line = new LyricLine { StartMs = timings[index], EndMs = timings[index + 1] };
                    line.Words.Add(new() { Word = text, StartMs = line.StartMs, DurationMs = line.EndMs - line.StartMs });
                    return line;
                }).ToList();
            source[2].TransLateText = "Translated lyric";
            UILyricsBus.Publish(source);
            IsPlayingBus.Publish(false);

            using var target = new CanvasRenderTarget(canvas.Device, TargetWidth, TargetHeight, 96);
            Color[] Draw(int fan = 0, int roll = 0, bool feather = false, bool translation = true, double blur = 5)
            {
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, true, 1, blur,
                    0, 0, 0, 500, false, false, 0.55, 0.85, 0, EasingType.Sine, EaseMode.Out,
                    anchor, 60, false, Colors.White, showTranslation: translation, showPronunciation: false,
                    isLyricsEdgeFeatheringEffectEnabled: feather, isFanLyricsEnabled: fan != 0, fanLyricsAngle: fan,
                    is3DLyricsEnabled: roll != 0, lyrics3DZAngle: roll, lyrics3DDepth: 450));
                for (int i = 0; i < 60; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
                using (var drawing = CanvasDrawingSessionScope.Create(target))
                {
                    drawing.Context.Clear(Colors.Transparent);
                    coordinator.OnDraw(canvas, drawing.Context);
                }
                return target.GetPixelColors();
            }

            Draw();
            var baseline = Draw();
            var fanPositive = Draw(fan: 36);
            var fanNegative = Draw(fan: -36);
            var rolled = Draw(roll: 30);
            var feathered = Draw(roll: 30, feather: true);
            var withoutBlur = Draw(roll: 30, blur: 0);
            var reset = Draw();
            int restoredDifference = reset.Where((color, index) => color != baseline[index]).Count();
            Check(restoredDifference == 0, $"Turning geometry off must restore the original layout pixels. Different pixels: {restoredDifference}.");
            var withoutTranslation = Draw(roll: 30, translation: false);
            foreach (var frame in new[] { fanPositive, fanNegative, rolled, feathered })
            {
                Check(frame.Where((color, index) => index / TargetWidth < surface.Top || index / TargetWidth >= surface.Bottom)
                        .All(color => color.A == 0),
                    "Projected pixels must stay within the actual surface's vertical viewport.");
            }

            int LeftInk(Color[] frame) => frame.Where((color, index) => index % TargetWidth < pane.Left && color.A > 8).Count();
            int LeftDifference(Color[] first, Color[] second)
                => first.Where((color, index) => index % TargetWidth < pane.Left && color != second[index]).Count();
            Check(LeftInk(fanPositive) > 50 && !fanPositive.SequenceEqual(fanNegative),
                $"The fan must paint outside the old pane and changing direction must move rows. Left pixels: {LeftInk(fanPositive)}/{LeftInk(fanNegative)}.");
            Check(LeftInk(rolled) > 50 && LeftDifference(rolled, withoutBlur) > 50,
                "The paused 3D frame must preserve sharp glyphs and blurred rows to the left of the pane.");
            Check(LeftDifference(rolled, withoutTranslation) > 50,
                "The translated layer must also affect pixels across the old pane boundary.");
            Check(LeftInk(feathered) > 50, "Enabling the edge feather must preserve cross-pane ink after projection.");

            var hittableFrame = Draw(roll: 30);
            var scrollField = typeof(LyricsRenderCoordinator).GetField("_smoothedScrollY", Members)!;
            var scroll = (double)scrollField.GetValue(coordinator)!;
            int hit = -1;
            for (int y = (int)surface.Top; y < surface.Bottom && hit < 0; y += 2)
            {
                for (int x = 2; x < pane.Left && hit < 0; x += 2)
                {
                    if (hittableFrame[y * TargetWidth + x].A < 32) continue;
                    hit = (int)HitTest.Invoke(coordinator, [new Point(x, y), scroll, pane.Height, anchor])!;
                }
            }
            Check(hit >= 0, "A visible transformed glyph across the old pane edge must remain hittable.");
            LyricsFontSizeBus.Publish(42);
            var enlarged = Draw(roll: -30);
            Check(LeftInk(enlarged) > 50 && !enlarged.SequenceEqual(rolled),
                "Larger lyric text must still paint outside the old layout column.");
            LyricsFontSizeBus.Publish(28);
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
    private static void VerifyAutomaticPointerOverflow(CanvasAnimatedControl canvas)
    {
        const int width = 1920;
        const int height = 1080;
        const int center = width / 2;
        var coordinator = new LyricsRenderCoordinator
        {
            Canvas = canvas,
            LyricsRegion = new Rect(center, 30, 900, 1020),
            LyricsViewport = new Rect(0, 0, width, height),
        };
        TimeProgressBus.SetClock(() => 3500);
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsFontSizeBus.Publish(42);
            void PublishSettings(double blur)
            {
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, true, 1, blur,
                    0, 0, 0, 500, false, false, 0.6, 0.9, 0, EasingType.Sine, EaseMode.Out,
                    0.5, 60, false, Colors.White, isLyricsEdgeFeatheringEffectEnabled: true,
                    is3DLyricsEnabled: true, isAuto3DLyricsEnabled: true));
            }

            PublishSettings(5);
            var rows = Enumerable.Range(0, 7).Select(i =>
            {
                var line = new LyricLine { StartMs = i * 1000, EndMs = (i + 1) * 1000 };
                line.Words.Add(new() { Word = "Pointer tilt sample " + i, StartMs = line.StartMs, DurationMs = 1000 });
                return line;
            }).ToList();
            rows[3].TransLateText = "The pointer moves across the playback surface";
            UILyricsBus.Publish(rows);
            IsPlayingBus.Publish(false);

            using var target = new CanvasRenderTarget(canvas.Device, width, height, 96);
            Color[] Draw(Vector2? pointer)
            {
                SetAutomaticPointer.Invoke(coordinator, [pointer]);
                for (int i = 0; i < 90; i++) coordinator.OnUpdate(canvas, TimeSpan.FromMilliseconds(16.6667));
                using (var drawing = CanvasDrawingSessionScope.Create(target))
                {
                    drawing.Context.Clear(Colors.Transparent);
                    coordinator.OnDraw(canvas, drawing.Context);
                }
                return target.GetPixelColors();
            }

            Draw(Vector2.Zero);
            var centered = Draw(Vector2.Zero);
            var pointer = new Vector2(0.97f, -0.95f);
            var tilted = Draw(pointer);
            var reset = Draw(null);
            int LeftInk(Color[] pixels, int threshold)
                => pixels.Where((color, index) => index % width < center && color.A > threshold).Count();
            Check(LeftInk(centered, 12) == 0 && LeftInk(tilted, 12) > 100 && LeftInk(tilted, 180) > 20,
                $"Automatic pointer tilt must move sharp and blurred ink across the middle. Left ink = {LeftInk(tilted, 12)}/{LeftInk(tilted, 180)}.");
            Check(reset.SequenceEqual(centered), "Leaving the playback surface must restore the centered automatic projection.");
            PublishSettings(0);
            var sharp = Draw(pointer);
            Check(tilted.Where((color, index) => index % width < center && color != sharp[index]).Count() > 50,
                "The tilted frame must preserve blurred lyric coverage across the old center line.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
    private static void VerifyFanFocus(CanvasAnimatedControl canvas, EasingType easing, EaseMode easeMode)
    {
        const int nextRow = 4;
        long position = 3984;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = Pane, LyricsViewport = Viewport };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsFontSizeBus.Publish(36);
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, true, 1, 0,
                0, 0, 0, 500, false, false, 1, 1, 0, easing, easeMode,
                0.5, 60, false, Colors.White, showTranslation: false, showPronunciation: false,
                isLyricsEdgeFeatheringEffectEnabled: false, isFanLyricsEnabled: true, fanLyricsAngle: 30));
            var source = Enumerable.Range(0, 9).Select(i =>
            {
                // The 120 ms row must settle before the very next entrance.
                int start = i switch { 5 => 4496, 6 => 4616, 7 => 4672, _ => i * 1000 };
                int end = i switch { 4 => 4496, 5 => 4616, 6 => 4672, _ => (i + 1) * 1000 };
                var line = new LyricLine { StartMs = start, EndMs = end };
                line.Words.Add(new() { Word = "Fan wave row " + i, StartMs = start, DurationMs = end - start });
                return line;
            }).ToList();
            UILyricsBus.Publish(source);
            IsPlayingBus.Publish(true);

            using var target = new CanvasRenderTarget(canvas.Device, TargetWidth, TargetHeight, 96);
            var frame = TimeSpan.FromMilliseconds(8);
            void DrawFrame()
            {
                coordinator.OnUpdate(canvas, frame);
                using var scope = CanvasDrawingSessionScope.Create(target);
                scope.Context.Clear(Colors.Transparent);
                coordinator.OnDraw(canvas, scope.Context);
            }

            DrawFrame();
            var rows = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var angleProperty = typeof(RenderLyricsLine).GetProperty("FanAngleTransition", Members)!;
            double Angle(int index) => ((ValueTransition<double>)angleProperty.GetValue(rows[index])!).Value;
            var focusField = typeof(LyricsRenderCoordinator).GetField("_currentLineIndex", Members)!;
            int Focus() => (int)focusField.GetValue(coordinator)!;
            var timingField = typeof(LyricsRenderCoordinator).GetField("_lineScrollTiming", Members)!;

            Check(Focus() == nextRow - 1, "The flow-wave fixture must start one row before the entrance.");
            double incomingAngle = Angle(nextRow);
            Check(Math.Abs(incomingAngle) > 0.01, "The incoming lyric must already be tilted.");
            position += 8;
            DrawFrame();
            position += 8;
            DrawFrame();
            var timing = (LyricScrollTiming)timingField.GetValue(coordinator)!;
            double delay = timing.GetLineTiming(nextRow).Delay;
            double firstFocusedAngle = Angle(nextRow);
            Check(Focus() == nextRow && (easing != EasingType.FlowWave || delay > frame.TotalSeconds)
                  && Math.Sign(firstFocusedAngle) == Math.Sign(incomingAngle)
                  && Math.Abs(firstFocusedAngle) < Math.Abs(incomingAngle)
                  && Math.Abs(firstFocusedAngle) > Math.Abs(incomingAngle) * 0.7,
                $"{easing}/{easeMode}: Focus must start turning without a snap or wave delay. Angle before={incomingAngle:F4}, first frame={firstFocusedAngle:F4} rad, flow delay={delay:F3}s.");
            IsPlayingBus.Publish(false);
            int intermediateStates = 0;
            double previousAngle = Math.Abs(firstFocusedAngle);

            for (int i = 0; i < 40; i++)
            {
                DrawFrame();
                double angle = Angle(nextRow);

                Check(Math.Abs(angle) <= previousAngle + 0.0001
                      && (angle == 0 || Math.Sign(angle) == Math.Sign(incomingAngle)),
                    "The focused row must ease monotonically to horizontal while paused.");
                if (Math.Abs(angle) > 0.001 && previousAngle - Math.Abs(angle) > 0.0001)
                    intermediateStates++;
                previousAngle = Math.Abs(angle);
            }

            Check(intermediateStates >= 3 && Math.Abs(Angle(nextRow)) < 0.001,
                "The paused current row must show multiple intermediate angles and become horizontal within 320 ms.");
            for (int i = 0; i < 120; i++) DrawFrame();
            Check(Math.Abs(Angle(nextRow - 1)) > 0.01,
                "The old current row must settle at its angled target after its own delay.");

            // Seek to a nonadjacent row while paused, then play across both closely spaced entrances.
            position = 2100;
            DrawFrame();
            Check(Focus() == 2 && Math.Abs(Angle(2)) < 0.001, "A backward seek must settle the current row immediately.");
            position = 6500;
            DrawFrame();
            Check(Focus() == 7 && Math.Abs(Angle(7)) < 0.001, "A forward seek must settle the current row immediately.");
            position = 3984;
            DrawFrame();
            IsPlayingBus.Publish(true);
            while (position < 4488)
            {
                position += 8;
                DrawFrame();
            }
            double nextIncomingAngle = Math.Abs(Angle(nextRow + 1));
            position += 8;
            DrawFrame();
            double quickFocusAngle = Math.Abs(Angle(nextRow + 1));
            Check(Focus() == nextRow + 1 && quickFocusAngle > nextIncomingAngle * 0.7 && quickFocusAngle < nextIncomingAngle,
                "A second entrance must rotate immediately from its current angle without snapping.");
            while (position < 4608)
            {
                position += 8;
                DrawFrame();
            }
            Check(Focus() == nextRow + 1 && Math.Abs(Angle(nextRow + 1)) < 0.001,
                "A 120 ms current row must be horizontal before the following entrance.");
            while (position < 4664)
            {
                position += 8;
                DrawFrame();
            }
            int veryShortRow = nextRow + 2;
            double interrupted = Angle(veryShortRow);
            double followingIncoming = Angle(veryShortRow + 1);
            Check(Focus() == veryShortRow && Math.Abs(interrupted) > 0.001,
                "The 56 ms row must still be rotating when the next one is about to enter.");
            position += 8;
            DrawFrame();
            double followingFirstFrame = Angle(veryShortRow + 1);
            Check(Focus() == veryShortRow + 1 && followingFirstFrame > followingIncoming * 0.7
                  && followingFirstFrame < followingIncoming
                  && Math.Abs(Angle(veryShortRow) - interrupted) < 0.05,
                "Interrupting the short turn must preserve both rows' current angles when retargeting.");
            IsPlayingBus.Publish(false);
            for (int i = 0; i < 160; i++) DrawFrame();
            Check(Math.Abs(Angle(veryShortRow + 1)) < 0.001 && Math.Abs(Angle(veryShortRow)) > 0.01,
                "After quick entrances the newest row must settle flat and the outgoing row reach its own angle.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
