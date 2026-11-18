using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using AnimatedWin2dControls.Messages;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinUIMusicPlayer.Helper;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wparam, nint lparam,
        uint flags, uint timeoutMs, out nuint result);

    private static async Task CheckMonitorMovesAsync(Window window, NowPlayingCanvas host, Grid root, PlaybackSceneCover cover)
    {
        var areas = DisplayArea.FindAll();
        DisplayArea? hdr = null, sdr = null;
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            using var info = DisplayInformation.CreateForDisplayId(area.DisplayId);
            var kind = info.GetAdvancedColorInfo().CurrentAdvancedColorKind;
            var bounds = area.OuterBounds;
            Console.WriteLine($"Output {area.DisplayId.Value:X}: {kind}, {bounds.Width}x{bounds.Height} at {bounds.X},{bounds.Y}");
            if (kind == DisplayAdvancedColorKind.HighDynamicRange) hdr ??= area;
            else sdr ??= area;
        }
        if (hdr is null || sdr is null)
            throw new InvalidOperationException("Physical monitor round-trip requires one active HDR and one active SDR display.");

        var canvas = FindChild<CanvasAnimatedControl>(host, "PART_Canvas") ?? throw new Exception("Missing SDR canvas");
        var panel = FindChild<CanvasSwapChainPanel>(host, "PART_HdrPanel") ?? throw new Exception("Missing HDR panel");
        uint hdrDpi = 0, sdrDpi = 0;
        ulong current = 0;
        bool active = false, failed = false;
        int hdrFrames = 0, sdrFrames = 0;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var original = window.AppWindow.Position;
        using var display = new PlaybackHdrDisplay(window.AppWindow, window.DispatcherQueue, (id, peak) =>
        {
            current = id;
            Console.WriteLine($"Playback display {id:X}, peak {peak} nit");
            host.SetHdrPresentation(Math.Min(240, peak), Math.Min(600, peak), true, id);
        }, ex => Console.Error.WriteLine(ex));

        bool marking = false;
        Exception? renderError = null;
        void OnError(object? sender, Exception ex) => Interlocked.CompareExchange(ref renderError, ex, null);
        CanvasSolidColorBrush? white = null;
        PlaybackSceneCoverDrawing? art = null;
        var title = new AnimatedTextBlock
        {
            Width = 120, Height = 42, VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 34, 34),
            Document = new AnimatedTextDocument(new AnimatedTextParagraph("Euterpe", 22, Microsoft.UI.Text.FontWeights.Bold, 30))
        };
        void DrawHdr(CanvasDrawingSession session, float _)
        {
            hdrFrames++;
            if (!Volatile.Read(ref marking))
            {
                if (art is null || !ReferenceEquals(art.Device, session.Device))
                {
                    art?.Dispose();
                    art = new PlaybackSceneCoverDrawing(cover, session.Device);
                }
                art.Draw(session, new Rect(10, 38, 92, 72), 7, 240, 600);
                return;
            }
            // Mark the full buffer to detect an old-DPI surface showing black at its edges.
            if (white is null || !ReferenceEquals(white.Device, session.Device))
            {
                white?.Dispose();
                white = CanvasSolidColorBrush.CreateHdr(session.Device, new Vector4(3f, 3f, 3f, 1f));
            }
            session.FillRectangle(0, 0, (float)host.ActualWidth, (float)host.ActualHeight, white);
        }
        void DrawSdr(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            if (Volatile.Read(ref marking)) args.DrawingSession.Clear(Colors.White);
            Interlocked.Increment(ref sdrFrames);
        }
        void OnPresentation(bool shown, bool error)
        {
            active = shown;
            failed = error;
            Console.WriteLine($"Scene HDR={shown}, failed={error}, panel={panel.Visibility}");
        }
        host.HdrPresentationChanged += OnPresentation;
        host.DrawHdrForeground += DrawHdr;
        canvas.Draw += DrawSdr;
        try
        {
            host.ExceptionOccurred += OnError;
            root.Children.Add(title);
            host.SetHdrTitle(title, new Rect(40, 60, 120, 42));
            host.IsSnowEnabled = true;
            host.LyricsRegion = new Rect(140, 20, 210, 130);
            UILyricsBus.Publish([new() { StartMs = 0, EndMs = 30000,
                Words = { new() { Word = "Euterpe ", StartMs = 0, DurationMs = 3000 },
                          new() { Word = "ひとつになって", StartMs = 3000, DurationMs = 27000 } } }]);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TimeProgressBus.SetClock(() => 1500 + clock.ElapsedMilliseconds);
            TimeProgressBus.Publish(1500);
            IsPlayingBus.Publish(true);
            host.SetHdrPresentation(0, 0, true, 0);
            display.SetEnabled(true);
            foreach (var target in new[] { hdr, sdr, hdr, sdr, hdr })
            {
                bool wantHdr = target == hdr;
                var work = target.WorkArea;
                var size = window.AppWindow.Size;
                var center = new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2);
                Console.WriteLine($"Moving probe to {target.DisplayId.Value:X}, HDR={wantHdr}");
                var from = window.AppWindow.Position;
                for (int step = 1; step <= 12; step++)
                {
                    window.AppWindow.Move(new PointInt32(from.X + (center.X - from.X) * step / 12,
                        from.Y + (center.Y - from.Y) * step / 12));
                    await Task.Delay(12);
                }
                await WaitUntilAsync(() => current == target.DisplayId.Value && active == wantHdr && !failed
                    && (panel.SwapChain is not null) == wantHdr, "new display and presentation ownership");
                int before = wantHdr ? hdrFrames : Volatile.Read(ref sdrFrames);
                await WaitUntilAsync(() => (wantHdr ? hdrFrames : Volatile.Read(ref sdrFrames)) >= before + 8, "eight frames and UI dispatcher after moving");
                uint dpi = GetDpiForWindow(hwnd);
                await WaitUntilAsync(() => Math.Abs(host.XamlRoot.RasterizationScale * 96 - dpi) < 1, "per-monitor XAML layout");
                if (wantHdr && panel.SwapChain is { } chain)
                {
                    var actual = chain.SizeInPixels;
                    if (Math.Abs(actual.Width - host.ActualWidth * dpi / 96) > 1 ||
                        Math.Abs(actual.Height - host.ActualHeight * dpi / 96) > 1 || Math.Abs(chain.Dpi - dpi) > 1)
                        throw new Exception($"Wrong swap chain {actual.Width}x{actual.Height} at {chain.Dpi} DPI");
                }
                if (Volatile.Read(ref renderError) is { } error) throw new Exception("Playback scene failed during monitor move", error);
                PointInt32[] scenePoints = [new((int)(25 * dpi / 96), (int)(48 * dpi / 96)),
                    new((int)(55 * dpi / 96), (int)(48 * dpi / 96)), new((int)(86 * dpi / 96), (int)(48 * dpi / 96))];
                if (!TrySample(hwnd, scenePoints, wantHdr ? "JPEG after move" : "SDR shader after move", out var scenePixels))
                    throw new Exception("Failed to sample the playing scene after the output changed");
                if (wantHdr)
                {
                    var reference = MemoryMarshal.Cast<byte, Half>(DrawCover(cover, 240, 600));
                    for (int tile = 0; tile < 3; tile++)
                        for (int c = 0; c < 3; c++)
                            if (Math.Abs(scenePixels[3 * tile + c] - (float)reference[4 * (4 + 8 * tile) + c]) > .13f)
                                throw new Exception("JPEG scene resources failed to follow the actual output");
                }
                else if (scenePixels.All(v => v < .005f) || scenePixels.All(v => v > .98f))
                    throw new Exception("The SDR shader did not replace the previous frame");
                Volatile.Write(ref marking, true);
                before = wantHdr ? hdrFrames : Volatile.Read(ref sdrFrames);
                await WaitUntilAsync(() => (wantHdr ? hdrFrames : Volatile.Read(ref sdrFrames)) >= before + 4, "reference frames after scene playback");
                int width = (int)(host.ActualWidth * dpi / 96), height = (int)(host.ActualHeight * dpi / 96);
                PointInt32[] samples = [new(16, 16), new(width - 18, 16), new(width / 2, height / 2),
                    new(16, height - 18), new(width - 18, height - 18)];
                if (!TrySample(hwnd, samples, wantHdr ? "HDR client corners and center" : "SDR client corners and center", out var rgb))
                    throw new Exception("Monitor transfer window capture failed");
                float expected = wantHdr ? 3f : 1f;
                if (rgb.Any(channel => Math.Abs(channel - expected) > .13f))
                    throw new Exception($"Expected {expected:F2} scRGB across the client area");
                // WM_NULL from a different thread requires the real HWND to dispatch a message.
                if (!await Task.Run(() => SendMessageTimeout(hwnd, 0, 0, 0, 2, 1500, out _) != 0))
                    throw new TimeoutException("The playback window stopped dispatching Win32 messages");
                Volatile.Write(ref marking, false);
                if (wantHdr) hdrDpi = dpi; else sdrDpi = dpi;
                Console.WriteLine($"PASS responsive {target.DisplayId.Value:X} at {dpi} DPI, client {width}x{height}, frames HDR={hdrFrames} SDR={sdrFrames}");
            }
            Console.WriteLine($"PASS two physical HDR/SDR round-trips at {hdrDpi}/{sdrDpi} DPI");

            // Two HDR outputs can report identical color capabilities and DPI. Only the ID changes here.
            var previous = panel.SwapChain;
            int lastFrame = hdrFrames;
            host.SetHdrPresentation(240, 600, true, current ^ (1UL << 63));
            await WaitUntilAsync(() => active && hdrFrames >= lastFrame + 3, "new output with identical HDR settings");
            if (previous is null || panel.SwapChain is null || ReferenceEquals(previous, panel.SwapChain))
                throw new Exception("Changing only the output ID retained the previous swap chain");
            host.SetHdrPresentation(240, 600, true, current);
            Console.WriteLine("PASS changing only the output ID rebuilt the HDR swap chain");
        }
        finally
        {
            host.ExceptionOccurred -= OnError;
            TimeProgressBus.SetClock(null);
            IsPlayingBus.Publish(false);
            UILyricsBus.Publish([]);
            host.SetHdrTitle(null, default);
            root.Children.Remove(title);
            host.IsSnowEnabled = false;
            display.SetEnabled(false);
            art?.Dispose();
            canvas.Draw -= DrawSdr;
            host.DrawHdrForeground -= DrawHdr;
            white?.Dispose();
            host.HdrPresentationChanged -= OnPresentation;
            window.AppWindow.Move(original);
        }
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T match && match.Name == name) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i), name) is { } child) return child;
        return null;
    }
}
