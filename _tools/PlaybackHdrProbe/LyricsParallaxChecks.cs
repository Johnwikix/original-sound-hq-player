using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRect = Windows.Foundation.Rect;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointInt32 position);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref PointInt32 point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    private static async Task CheckLyricsParallaxAsync(Window window)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        bool sdrOnly = Environment.GetCommandLineArgs().Contains("--sdr-only");
        bool replay = Environment.GetCommandLineArgs().Contains("--replay-parallax");
        if (replay) Console.WriteLine("Replaying normalized pointer coordinates without moving the physical cursor.");
        var output = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary);
        if (sdrOnly)
        {
            var outputs = DisplayArea.FindAll();
            for (int i = 0; i < outputs.Count; i++)
            {
                var candidate = outputs[i];
                if (candidate.IsPrimary) continue;
                output = candidate;
                break;
            }
        }
        using var display = DisplayInformation.CreateForDisplayId(output.DisplayId);
        bool hdrOutput = display.GetAdvancedColorInfo().CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
        var work = output.WorkArea;
        window.AppWindow.Move(new PointInt32(work.X + work.Width / 3, work.Y + work.Height / 6));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        window.Activate();
        ShowWindow(hwnd, 5);
        double scale = GetDpiForWindow(hwnd) / 96d;
        window.AppWindow.ResizeClient(new SizeInt32((int)(800 * scale), (int)(650 * scale)));
        GetCursorPos(out var original);

        try
        {
            if (hdrOutput && !sdrOnly) await CheckPresentation("Fluid HDR", fluid: true, hdr: true);
            else Console.WriteLine("Routed-pointer probe is using the SDR output.");
            await CheckPresentation("Fluid SDR", fluid: true, hdr: false);
            await CheckPresentation("Acrylic", fluid: false, hdr: true);
        }
        finally
        {
            window.SystemBackdrop = null;
            UILyricsBus.Publish([]);
            if (!replay) SetCursorPos(original.X, original.Y);
        }

        async Task CheckPresentation(string label, bool fluid, bool hdr)
        {
            var vm = new AppViewModel
            {
                IsFluidBackgroundEnabled = fluid, PlaybackHdrEnabled = hdr,
                PlaybackHdrDisplayId = output.DisplayId.Value, PlaybackHdrWhiteNits = 200,
                PlaybackHdrEffectivePeakNits = 600, Is3DLyricsEnabled = true, IsAuto3DLyricsEnabled = true
            };
            App.MainWindow = window;
            using var page = new PlayingDetailPage(vm);
            page.AddSpatialProbeTitle();
            if (fluid)
            {
                var teal = new byte[32 * 32 * 4];
                for (int i = 0; i < teal.Length; i += 4)
                {
                    teal[i] = 8;
                    teal[i + 1] = (byte)(20 + i % 39);
                    teal[i + 2] = 27;
                    teal[i + 3] = 255;
                }
                page.ConfigureBackgroundCanvas = canvas => canvas.SetBackdrop(null, new ArtworkPixelData(teal));
            }
            window.SystemBackdrop = fluid ? null : new CustomAcrylicSystemBackdrop(window) { TintOpacity = .7 };
            window.Content = page;
            int moves = 0, exits = 0;
            PointerEventHandler onMove = (_, _) => moves++;
            PointerEventHandler onExit = (_, _) => exits++;
            page.AddHandler(UIElement.PointerMovedEvent, onMove, true);
            page.AddHandler(UIElement.PointerExitedEvent, onExit, true);
            try
            {
                await WaitUntilAsync(() => page.LyricsPane.Width > 200 &&
                    (fluid ? page.BackgroundCanvas is not null : page.StandaloneLyrics is not null), $"{label} host");
                if (hdr && fluid) await WaitUntilAsync(() => vm.PlaybackHdrActive, "Fluid HDR swap chain");
                if (!replay) await FindPointerPlacementAsync(page.ActualWidth, page.ActualHeight);
                object host = fluid ? page.BackgroundCanvas! : page.StandaloneLyrics;
                object coordinator = host.GetType().GetField("_coordinator", fields)!.GetValue(host)!;
                var pointer = typeof(PlayingDetailPage).GetField("_spatialPointer", fields)!;
                var projection = coordinator.GetType().GetField("_lyricsProjection", fields)!;
                var spatialClock = typeof(PlayingDetailPage).GetField("_spatialClock", fields)!;
                var pageProjection = typeof(PlayingDetailPage).GetField("_spatialProjection", fields)!;
                UILyricsBus.Publish([]);
                SetReplayPointer(new Vector2(.7f, -.6f));
                await WaitUntilAsync(() => pageProjection.GetValue(page) is Matrix4x4 m && !m.IsIdentity
                    && page.Cover.Projection is not null && page.IsSpatialProbeTitleReady, $"{label} foreground tilt without lyrics");
                page.CheckSpatialVisuals();
                var tracksLayout = typeof(PlayingDetailPage).GetField("_trackingSpatialLayout", fields)!;
                var tracksPointer = typeof(PlayingDetailPage).GetField("_trackingSpatialPointer", fields)!;
                vm.IsPlayingDetailVisible = false;
                await WaitUntilAsync(() => spatialClock.GetValue(page) is null && pointer.GetValue(page) is null
                    && tracksLayout.GetValue(page) is false && tracksPointer.GetValue(page) is false,
                    $"{label} hidden playback page stops callbacks");
                vm.IsPlayingDetailVisible = true;
                await WaitUntilAsync(() => tracksLayout.GetValue(page) is true && tracksPointer.GetValue(page) is true,
                    $"{label} visible playback page reinstates callbacks");
                await WaitUntilAsync(() => spatialClock.GetValue(page) is null && page.Cover.Projection is null,
                    $"{label} neutral foreground stops its frame subscription");
                PublishParallaxLyrics();
                if (replay) SetReplayPointer(null);
                else Move(page.ActualWidth / 2, page.ActualHeight / 2);
                await Task.Delay(900);
                var render = (LyricsRenderCoordinator)coordinator;
                var baseline = CountRowInk(page, render, hdr && fluid, fluid);
                Console.WriteLine($"{label} neutral: {FormatInk(baseline)}");
                if (!baseline.Any(row => row.Value.Primary >= 6 && row.Value.Translation >= 3))
                    throw new InvalidOperationException($"{label}: missing separate primary and translation baselines.");

                var points = new[] { (.15, .26), (.86, .27) };
                foreach (var (x, y) in points)
                {
                    if (replay)
                    {
                        SetReplayPointer(new Vector2((float)(x * 2 - 1), (float)(y * 2 - 1)));
                    }
                    else
                    {
                        int before = moves;
                        Move(page.ActualWidth * x, page.ActualHeight * y);
                        await WaitUntilAsync(() => moves > before && pointer.GetValue(page) is Vector2 p &&
                            Math.Abs(p.X - (x * 2 - 1)) < .1 && Math.Abs(p.Y - (y * 2 - 1)) < .1,
                            $"{label} production routed pointer from ({x:F2}, {y:F2})");
                    }
                    await WaitUntilAsync(() => projection.GetValue(coordinator) is Matrix4x4 m && !m.IsIdentity,
                        $"{label} 3D transform");
                    await Task.Delay(300);
                    var measured = CountRowInk(page, render, hdr && fluid, fluid);
                    Console.WriteLine($"{label} pointer {pointer.GetValue(page)}: {FormatInk(measured)}");
                    int verifiedRows = 0;
                    foreach (var (row, neutral) in baseline)
                    {
                        if (neutral.Primary < 6 || neutral.Translation < 3 || !measured.TryGetValue(row, out var tilted))
                            continue;
                        if (tilted.Translation < 3 || tilted.Primary < neutral.Primary * .30)
                            throw new InvalidOperationException($"{label}: row {row} changed from {neutral.Primary}/{neutral.Translation} to {tilted.Primary}/{tilted.Translation} primary/translation samples.");
                        verifiedRows++;
                    }
                    if (verifiedRows == 0) throw new InvalidOperationException($"{label}: no non-current row verified after tilting.");
                }
                if (replay)
                {
                    SetReplayPointer(null);
                }
                else
                {
                    int previousExits = exits;
                    Move(page.ActualWidth / 2, page.ActualHeight + 28);
                    await WaitUntilAsync(() => exits > previousExits && pointer.GetValue(page) is null,
                        $"{label} routed pointer exit");
                }

                SetReplayPointer(new Vector2(.8f, -.7f));
                await WaitUntilAsync(() => spatialClock.GetValue(page) is not null, $"{label} clock starts");
                window.Content = null;
                await WaitUntilAsync(() => spatialClock.GetValue(page) is null && page.Cover.Projection is null,
                    $"{label} unload clears the projection and releases its frame subscription");

                void SetReplayPointer(Vector2? position)
                    => typeof(PlayingDetailPage).GetMethod("SetSpatialPointer", fields)!.Invoke(page, [position]);
            }
            finally
            {
                page.RemoveHandler(UIElement.PointerMovedEvent, onMove);
                page.RemoveHandler(UIElement.PointerExitedEvent, onExit);
                window.Content = null;
            }
        }

        async Task FindPointerPlacementAsync(double width, double height)
        {
            int physicalWidth = (int)(width * scale);
            int physicalHeight = (int)(height * scale);
            int left = work.X + 20;
            int right = work.X + work.Width - physicalWidth - 32;
            int top = work.Y + 20;
            int bottom = work.Y + work.Height - physicalHeight - 100;
            PointInt32[] candidates = [new(right, top), new(left, top), new(right, bottom), new(left, bottom),
                new((left + right) / 2, (top + bottom) / 2)];
            foreach (var candidate in candidates)
            {
                window.AppWindow.Move(candidate);
                SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x40);
                SetForegroundWindow(hwnd);
                await Task.Delay(130);
                if (IsOwnWindowAt(.15, .26) && IsOwnWindowAt(.86, .27) && IsOwnWindowAt(.5, .5)) return;
            }
            throw new InvalidOperationException("No unobstructed desktop location for the routed-pointer probe.");

            bool IsOwnWindowAt(double x, double y)
            {
                var point = new PointInt32((int)(width * x * scale), (int)(height * y * scale));
                if (!ClientToScreen(hwnd, ref point)) return false;
                var hit = WindowFromPoint(point);
                GetWindowThreadProcessId(hit, out uint processId);
                if (hit == hwnd || GetAncestor(hit, 2) == hwnd || processId == Environment.ProcessId)
                    return true;
                Console.WriteLine($"Probe position covered by PID {processId} at physical ({point.X},{point.Y}).");
                return false;
            }
        }

        void Move(double x, double y)
        {
            var point = new PointInt32((int)(x * scale), (int)(y * scale));
            if (!ClientToScreen(hwnd, ref point) || !SetCursorPos(point.X, point.Y))
                throw new InvalidOperationException("Could not drive test pointer.");
        }

        static string FormatInk(Dictionary<int, (int Primary, int Translation)> values)
            => string.Join(", ", values.Select(pair => $"row {pair.Key}={pair.Value.Primary}/{pair.Value.Translation}"));

        Dictionary<int, (int Primary, int Translation)> CountRowInk(PlayingDetailPage page, LyricsRenderCoordinator render, bool hdr, bool fluid)
        {
            var renderType = typeof(LyricsRenderCoordinator);
            var lines = ((List<RenderLyricsLine>)renderType.GetField("_renderLines", fields)!.GetValue(render)!).ToArray();
            var transform = (Matrix4x4)renderType.GetField("_lyricsProjection", fields)!.GetValue(render)!;
            var mouse = (ValueTransition<double>)renderType.GetField("_mouseYScrollTransition", fields)!.GetValue(render)!;
            var hovered = (int)renderType.GetField("_hoveredLineIndex", fields)!.GetValue(render)!;
            var current = (int)renderType.GetField("_currentLineIndex", fields)!.GetValue(render)!;
            var region = render.LyricsRegion;
            var origin = fluid ? default : page.StandaloneLyrics.TransformToVisual(page).TransformPoint(default);
            var coordinates = new List<PointInt32>();
            var ranges = new List<(int Row, int Primary, int Secondary, int End)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var row = lines[i];
                if (i == current || row.PrimaryTextLayout is not { } primary || row.TranslationLayer.Layout is not { } secondary)
                    continue;
                var size = new Vector2((float)region.X, (float)(region.Y + region.Height * .35 + row.ScrollMotion.Value + mouse.Value));
                var affine = Matrix3x2.CreateScale((float)row.ScaleTransition.Value, row.CenterPosition) * Matrix3x2.CreateTranslation(size);
                var midpoint = Project(row.PrimaryPosition + new Vector2(50, 0), affine);
                if (midpoint.Y < page.ActualHeight * .12 || midpoint.Y > page.ActualHeight * .82) continue;

                int p = coordinates.Count;
                AddLayer(primary.DrawBounds, row.PrimaryPosition, affine);
                int q = coordinates.Count;
                AddLayer(secondary.DrawBounds, row.TranslationLayer.Position, affine);
                if (coordinates.Count <= 1024 && q - p >= 40 && coordinates.Count - q >= 40)
                    ranges.Add((i, p, q, coordinates.Count));
                else
                    coordinates.RemoveRange(p, coordinates.Count - p);
            }
            var rgb = new float[coordinates.Count * 3];
            if (coordinates.Count == 0) return new();
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, coordinates.ToArray(), coordinates.Count, rgb));
            Console.Write($"[current {current}, hover {hovered}] ");
            return ranges.ToDictionary(range => range.Row,
                range => (Ink(range.Primary, range.Secondary), Ink(range.Secondary, range.End)));

            int Ink(int from, int to) => Enumerable.Range(from, to - from).Count(i =>
                rgb[3 * i] > (hdr ? .12f : .07f) && rgb[3 * i] > rgb[3 * i + 1] + .06f &&
                rgb[3 * i + 2] > rgb[3 * i + 1] + .03f);

            Vector2 Project(Vector2 input, Matrix3x2 affine)
            {
                var point = Vector2.Transform(input, affine);
                var projected = Vector4.Transform(new Vector4(point, 0, 1), transform);
                return new(projected.X / projected.W + (float)origin.X,
                    projected.Y / projected.W + (float)origin.Y);
            }

            void AddLayer(WinRect bounds, Vector2 position, Matrix3x2 affine)
            {
                for (int y = 0; y < 9; y++)
                for (int x = 0; x < 13; x++)
                {
                    var sample = position + new Vector2(
                        (float)(bounds.X + Math.Min(bounds.Width, 118) * (x + .5) / 13),
                        (float)(bounds.Y + Math.Min(bounds.Height, 38) * (y + .5) / 9));
                    var projected = Project(sample, affine);
                    if (projected.X > 1 && projected.X < page.ActualWidth - 1
                        && projected.Y > 1 && projected.Y < page.ActualHeight - 1)
                        coordinates.Add(new((int)Math.Round(scale * projected.X), (int)Math.Round(scale * projected.Y)));
                }
            }
        }
    }

    private static void PublishParallaxLyrics()
    {
        LyricsSettingsBus.Publish(new LyricsSettingsBus.Settings("Segoe UI", CanvasHorizontalAlignment.Left,
            true, 1, 5, 4, 5, 110, 1200, true, true, .45, .60, 0, EasingType.FlowWave,
            EaseMode.FlowWave, .35, 60, true, ColorHelper.FromArgb(255, 255, 0, 185),
            is3DLyricsEnabled: true, isAuto3DLyricsEnabled: true));
        LyricsFontSizeBus.Publish(42);
        var lines = Enumerable.Range(0, 9).Select(i =>
        {
            var line = new LyricLine { StartMs = i * 2400, EndMs = (i + 1) * 2400, TransLateText = "     MMMMM translation" };
            line.Words.Add(new LyricWord { Word = "MMMM main lyric", StartMs = i * 2400, DurationMs = 2400 });
            return line;
        }).ToList();
        UILyricsBus.Publish(lines);
        IsPlayingBus.Publish(false);
        TimeProgressBus.Publish(10400);
    }
}
