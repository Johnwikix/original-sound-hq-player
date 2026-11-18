using System.Numerics;
using System.Runtime.InteropServices;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    private static async Task CheckPlaybackNavigationEntranceAsync(Window window, Grid root, ulong displayId,
        double white, double peak)
    {
        const int frameLimit = 96;
        root.Background = new SolidColorBrush(Colors.Blue);
        var frame = new Frame { Visibility = Visibility.Collapsed };
        root.Children.Add(frame);
        WinUIMusicPlayer.App.MainWindow = window;
        var model = new AppViewModel
        {
            IsFluidBackgroundEnabled = true,
            PlaybackHdrEnabled = true,
            PlaybackHdrDisplayId = displayId,
            PlaybackHdrWhiteNits = white,
            PlaybackHdrEffectivePeakNits = peak
        };
        using var page = new PlayingDetailPage(model);
        CanvasSolidColorBrush? marker = null;
        CanvasDevice? markerDevice = null;
        bool failFirstDraw = false;
        int revealCount = 0;
        int completionCount = 0;
        bool prematureReveal = false;
        var failure = new InvalidOperationException("Injected navigation first-frame failure.");
        page.ConfigureBackgroundCanvas = canvas =>
        {
            canvas.DrawHdrForeground += DrawMarker;
            canvas.ApplyTemplate();
            var sdr = FindChild<CanvasAnimatedControl>(canvas, "PART_Canvas") ?? throw new Exception("Missing navigation SDR canvas.");
            sdr.Draw += (_, e) => e.DrawingSession.Clear(Colors.Red);
        };
        using var services = new ServiceCollection().AddSingleton(page).BuildServiceProvider();
        var navigation = new NavigationService(services, NullLogger<NavigationService>.Instance);
        navigation.Initialize(frame);
        navigation.RegisterPage<PlayingDetailPage>();
        window.Activate();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 5);
        await WaitUntilAsync(() => root.ActualWidth > 0, "navigation fixture arrangement");
        var scale = GetDpiForWindow(hwnd) / 96d;
        var points = new[] { new PointInt32((int)(280 * scale), (int)(160 * scale)) };
        try
        {
            await CheckFirstVisibleFrameAsync("HDR navigation", Show);
            await DismissAsync();

            var canvas = page.BackgroundCanvas ?? throw new InvalidOperationException("The playback scene did not load.");
            canvas.SetWindowPaused(true);
            Show();
            await Task.Delay(1450);
            if (!navigation.IsPreparingShow || frame.Opacity != 0 || frame.IsHitTestVisible
                || revealCount != 1 || completionCount != 1 || model.PlaybackHdrActive)
                throw new InvalidOperationException("Slow HDR setup exposed an SDR frame or completed before a valid HDR frame.");
            await CheckFirstVisibleFrameAsync("HDR navigation after delayed readiness", () => canvas.SetWindowPaused(false));
            await DismissAsync();

            canvas.SetWindowPaused(true);
            Show();
            if (!navigation.IsPreparingShow)
                throw new InvalidOperationException("The cancel test did not enter the deferred HDR branch.");
            model.IsPlayingDetailVisible = false;
            navigation.Dismiss();
            if (navigation.IsPreparingShow || frame.Visibility != Visibility.Collapsed || frame.Opacity != 1 || !frame.IsHitTestVisible
                || revealCount != 2 || completionCount != 3)
                throw new InvalidOperationException("Canceling the pending HDR entrance revealed it or left navigation busy.");
            canvas.SetWindowPaused(false);

            failFirstDraw = true;
            Show();
            await WaitUntilAsync(() => completionCount == 4 && model.PlaybackHdrFailed, "navigation fallback");
            var fallback = new float[3];
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, points, points.Length, fallback));
            if (model.PlaybackHdrActive || frame.Opacity != 1 || fallback[0] < .3f || fallback[1] > .1f || fallback[2] > .1f)
                throw new InvalidOperationException("The page did not expose a current SDR frame after a first-frame failure.");
            await DismissAsync();
            failFirstDraw = false;
            await CheckFirstVisibleFrameAsync("HDR navigation after cancel and failure", Show);
        }
        finally
        {
            frame.Content = null;
            root.Children.Remove(frame);
            marker?.Dispose();
        }
        Console.WriteLine("PASS production navigation reveal, delayed readiness, cancellation, fallback and re-entry");

        void DrawMarker(CanvasDrawingSession session, float _)
        {
            if (failFirstDraw) throw failure;
            if (!ReferenceEquals(markerDevice, session.Device))
            {
                marker?.Dispose();
                markerDevice = session.Device;
                marker = CanvasSolidColorBrush.CreateHdr(session.Device, new Vector4(0, 2.5f, 0, 1));
            }
            session.FillRectangle(255, 125, 65, 85, marker);
        }

        void Show()
        {
            model.IsPlayingDetailVisible = true;
            navigation.Show(typeof(PlayingDetailPage), onCompleted: () => completionCount++, onRevealing: () =>
            {
                revealCount++;
                bool animated = frame.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue;
                Console.WriteLine($"Navigation reveal opacity={frame.Opacity}, HDR={page.BackgroundCanvas?.IsHdrFramePresented}, cover preview={page.Cover.Opacity}, animated={animated}");
                prematureReveal |= !failFirstDraw && (frame.Opacity != 0 || page.BackgroundCanvas?.IsHdrFramePresented != true
                    || page.Cover.Opacity != 0 || animated);
            });
        }

        async Task DismissAsync()
        {
            bool closed = false;
            model.IsPlayingDetailVisible = false;
            navigation.Dismiss(80, () => closed = true);
            await WaitUntilAsync(() => closed && frame.Visibility == Visibility.Collapsed, "navigation dismissal");
        }

        async Task CheckFirstVisibleFrameAsync(string label, Action start)
        {
            var pixels = new float[frameLimit * points.Length * 3];
            var times = new long[frameLimit];
            var capture = Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(SampleMotionFrames(hwnd, points, points.Length, pixels, times, frameLimit, out int count));
                return count;
            });
            await Task.Delay(120);
            start();
            await WaitUntilAsync(() => model.PlaybackHdrActive && frame.Opacity == 1, "navigation HDR frame");
            int count = await capture;
            int firstChange = -1;
            int firstHdr = -1;
            for (int i = 0; i < count; ++i)
            {
                var (r, g, b) = (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]);
                if (firstChange < 0 && (r > .1f || g > .1f || b < .2f)) firstChange = i;
                if (firstHdr < 0 && g > 2.2f && r < .12f && b < .12f) firstHdr = i;
            }
            Console.WriteLine($"{label} WGC frames={count}, first visible change={firstChange}, first HDR={firstHdr}");
            if (firstChange <= 0 || firstChange != firstHdr || prematureReveal)
                throw new InvalidOperationException("The first visible playback-navigation frame was not the complete HDR frame over the previous page.");
        }
    }

    private static async Task CheckHdrHandoffAsync(Window window, Grid root)
    {
        var areas = DisplayArea.FindAll();
        DisplayArea? output = null;
        double peak = 0;
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            using var display = DisplayInformation.CreateForDisplayId(area.DisplayId);
            var color = display.GetAdvancedColorInfo();
            if (color.CurrentAdvancedColorKind != DisplayAdvancedColorKind.HighDynamicRange) continue;
            output = area;
            peak = Math.Min(600, color.MaxLuminanceInNits);
            break;
        }

        if (output is null) throw new InvalidOperationException("The HDR handoff check requires an HDR output.");
        double white = Math.Min(200, peak);
        int width = 640, height = 420;
        var work = output.WorkArea;
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));

        await CheckPlaybackNavigationEntranceAsync(window, root, output.DisplayId.Value, white, peak);

        root.Background = new SolidColorBrush(Colors.Black);
        var overlay = new Grid();
        using var host = new NowPlayingCanvas();
        var preview = new Border
        {
            Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16),
            Background = new SolidColorBrush(Colors.Blue)
        };
        var title = new AnimatedTextBlock
        {
            Width = 120, Height = 48, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(104, 16, 0, 0),
            Foreground = new SolidColorBrush(Colors.White),
            Document = new AnimatedTextDocument(new AnimatedTextParagraph("████", 28, Microsoft.UI.Text.FontWeights.Bold, 35))
        };
        overlay.Children.Add(host);
        overlay.Children.Add(preview);
        overlay.Children.Add(title);
        root.Children.Add(overlay);

        // An entrance storyboard can hold the containing frame at either of these values.
        var translate = new TranslateTransform { Y = 12 };
        overlay.RenderTransform = translate;
        overlay.Opacity = .7;
        _ = host.SetHdrNavigationTransitionAsync(true);
        window.Activate();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 5);
        await WaitUntilAsync(() => host.IsLoaded && title.IsLoaded && host.ActualWidth > 0, "playback entrance loaded");
        var sdr = FindChild<CanvasAnimatedControl>(host, "PART_Canvas") ?? throw new Exception("Missing SDR canvas.");
        var panel = FindChild<CanvasSwapChainPanel>(host, "PART_HdrPanel") ?? throw new Exception("Missing HDR panel.");
        var titleCanvas = FindChild<CanvasControl>(title, "AnimatedCanvas") ?? throw new Exception("Missing SDR title canvas.");
        int sdrFrames = 0, hdrFrames = 0, committed = 0;
        int titleFrames = 0, fallbackTitleFrames = -1, fallbackSdrFrames = -1;
        bool useNewSdrFrame = false;
        int firstSdrOpacity = -1, firstTitleOpacity = -1, firstPreviewOpacity = -1;
        bool firstPanelVisible = false;
        bool active = false, failed = false, coverReady = false, coverDrawn = false, injectFailure = false;
        var injected = new InvalidOperationException("Injected first-frame presentation failure");
        CanvasSolidColorBrush? cover = null;
        sdr.Draw += DrawSdr;
        titleCanvas.Draw += ObserveTitle;
        host.HdrPresentationChanged += OnPresentationChanged;
        host.DrawHdrForeground += DrawHdr;
        host.HdrFramePresented += CommitCover;
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref sdrFrames) >= 3, "SDR entrance frames");
            host.SetHdrPresentation(white, peak, true, output.DisplayId.Value);
            await Task.Delay(200);
            Console.WriteLine($"Entrance presentation HDR={active}, frames={hdrFrames}, attached={panel.SwapChain is not null}");
            if (active || panel.SwapChain is not null || hdrFrames != 0)
                throw new InvalidOperationException("HDR appeared during the SDR page animation.");

            var intro = Sample("During opacity/slide entrance", (280, 58));
            if (intro[0] < .30f || intro[1] > .12f)
                throw new InvalidOperationException("The SDR preview did not survive the entry animation.");
            overlay.Opacity = 1;
            overlay.RenderTransform = null;
            await host.SetHdrNavigationTransitionAsync(false);
            await WaitUntilAsync(() => active && committed > 0, "first completed HDR frame");
            if (firstSdrOpacity != 0 || firstTitleOpacity != 1 || firstPreviewOpacity != 1 || !firstPanelVisible)
                throw new InvalidOperationException("The first successful HDR commit did not replace the background while keeping unready title and cover previews.");
            await TestLayerColorsAsync("HDR without the late cover", expectedCoverBlue: true);
            if (titleCanvas.Opacity != 1)
                throw new InvalidOperationException("The SDR title disappeared ahead of a valid HDR title draw.");

            host.SetHdrTitle(title, new Rect(104, 16, 120, 48));
            await WaitUntilAsync(() => titleCanvas.Opacity == 0, "HDR title replacement");
            coverReady = true;
            await WaitUntilAsync(() => preview.Opacity == 0, "HDR cover ownership after present");
            await TestLayerColorsAsync("HDR title and delayed cover committed", expectedCoverBlue: false);

            int beforeFallback = Volatile.Read(ref sdrFrames);
            int beforeTitleFallback = titleFrames;
            // Only the HDR path has been drawing since the SDR preview became hidden.
            // Change both inputs and verify the replacement frame is drawn while hidden.
            title.Document = new AnimatedTextDocument(
                new AnimatedTextParagraph("████", 28, Microsoft.UI.Text.FontWeights.Bold, 35));
            title.Foreground = new SolidColorBrush(Colors.Magenta);
            Volatile.Write(ref useNewSdrFrame, true);
            host.SetHdrPresentation(0, 0, true, output.DisplayId.Value);
            await WaitUntilAsync(() => !active && panel.SwapChain is null && sdr.Opacity == 1
                && titleCanvas.Opacity == 1 && preview.Opacity == 1, "SDR fallback");
            if (fallbackSdrFrames <= beforeFallback || fallbackTitleFrames <= beforeTitleFallback)
                throw new InvalidOperationException("An old background or title was exposed before its SDR canvas redrew.");
            await TestLayerColorsAsync("SDR fallback", expectedCoverBlue: true, hdr: false);
            var current = Sample("Updated SDR background and title", (280, 58), (121, 42), (129, 42), (137, 42));
            if (current[0] > 1.8f || !Enumerable.Range(1, 3).Any(i => current[i * 3] > .5f
                && current[i * 3 + 1] < .3f && current[i * 3 + 2] > .5f))
                throw new InvalidOperationException("The freshly restored SDR plane still showed the previous color or title.");

            injectFailure = true;
            host.SetHdrPresentation(white, peak, true, output.DisplayId.Value);
            await WaitUntilAsync(() => !active && failed, "injected first present failure");
            if (panel.SwapChain is not null || titleCanvas.Opacity != 1 || preview.Opacity != 1)
                throw new InvalidOperationException("A failed first frame replaced the SDR preview.");
            await TestLayerColorsAsync("SDR fallback after failure", expectedCoverBlue: true, hdr: false);

            injectFailure = false;
            host.RetryHdrPresentation();
            await WaitUntilAsync(() => active && preview.Opacity == 0 && titleCanvas.Opacity == 0, "HDR retry");
            await host.SetHdrNavigationTransitionAsync(true);
            overlay.RenderTransform = translate;
            overlay.Opacity = .7;
            await WaitUntilAsync(() => !active && sdr.Opacity == 1 && titleCanvas.Opacity == 1 && preview.Opacity == 1,
                "SDR presentation during dismiss");
            int lastFrame = hdrFrames;
            await Task.Delay(120);
            if (hdrFrames != lastFrame || panel.SwapChain is not null)
                throw new InvalidOperationException("HDR kept drawing during dismiss.");

            host.PauseRendering();
            overlay.Visibility = Visibility.Collapsed;
            coverReady = false;
            host.ResumeRendering();
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 1;
            overlay.RenderTransform = null;
            await host.SetHdrNavigationTransitionAsync(false);
            await WaitUntilAsync(() => active && hdrFrames > lastFrame + 2, "HDR re-entry");
            await TestLayerColorsAsync("Same-song re-entry before the new cover is ready", expectedCoverBlue: true);
            Console.WriteLine("PASS HDR first-frame, delayed-cover, title, failure, dismissal and re-entry handoffs");
        }
        finally
        {
            host.HdrFramePresented -= CommitCover;
            host.DrawHdrForeground -= DrawHdr;
            host.HdrPresentationChanged -= OnPresentationChanged;
            sdr.Draw -= DrawSdr;
            titleCanvas.Draw -= ObserveTitle;
            cover?.Dispose();
            title.PrepareForShutdown();
        }

        void CommitCover()
        {
            preview.Opacity = coverDrawn ? 0 : 1;
            if (++committed == 1)
            {
                firstSdrOpacity = (int)sdr.Opacity;
                firstTitleOpacity = (int)titleCanvas.Opacity;
                firstPreviewOpacity = (int)preview.Opacity;
                firstPanelVisible = panel.Visibility == Visibility.Visible && panel.SwapChain is not null;
            }
        }

        void OnPresentationChanged(bool shown, bool error)
        {
            active = shown;
            failed = error;
            if (!shown)
            {
                fallbackTitleFrames = titleFrames;
                fallbackSdrFrames = Volatile.Read(ref sdrFrames);
                preview.Opacity = 1;
                cover?.Dispose();
                cover = null;
            }
        }

        void DrawSdr(ICanvasAnimatedControl _, CanvasAnimatedDrawEventArgs e)
        {
            e.DrawingSession.Clear(Volatile.Read(ref useNewSdrFrame) ? Colors.DarkRed : Colors.Red);
            Interlocked.Increment(ref sdrFrames);
        }

        void ObserveTitle(CanvasControl _, CanvasDrawEventArgs __) => titleFrames++;

        void DrawHdr(CanvasDrawingSession session, float _)
        {
            hdrFrames++;
            coverDrawn = coverReady;
            session.Clear(Colors.Green);
            if (coverDrawn)
            {
                cover ??= CanvasSolidColorBrush.CreateHdr(session.Device, new Vector4(2.5f, 2.5f, 0, 1));
                session.FillRectangle(16, 16, 50, 50, cover);
            }
            if (injectFailure) throw injected;
        }

        async Task TestLayerColorsAsync(string label, bool expectedCoverBlue, bool hdr = true)
        {
            await Task.Delay(120);
            var colors = Sample(label, (40, 40), (280, 58));
            bool coverOk = expectedCoverBlue ? colors[2] > .3f && colors[0] < .15f
                : colors[0] > 2.2f && colors[1] > 2.2f && colors[2] < .12f;
            bool backdropOk = hdr ? colors[4] > .10f && colors[3] < .12f
                : colors[3] > .30f && colors[4] < .12f;
            if (!coverOk || !backdropOk)
                throw new Exception($"{label} showed a blank or stale layer.");
        }

        float[] Sample(string label, params (int X, int Y)[] dipPoints)
        {
            double scale = GetDpiForWindow(hwnd) / 96d;
            var points = dipPoints.Select(p => new PointInt32((int)(p.X * scale), (int)(p.Y * scale))).ToArray();
            var pixels = new float[points.Length * 3];
            int result = SampleFrame(hwnd, true, points, points.Length, pixels);
            Marshal.ThrowExceptionForHR(result);
            Console.WriteLine($"{label}: {string.Join(", ", pixels.Select(p => p.ToString("F3")))}");
            return pixels;
        }
    }
}
