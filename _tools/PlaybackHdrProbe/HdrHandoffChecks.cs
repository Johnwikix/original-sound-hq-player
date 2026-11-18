using System.Diagnostics;
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
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern int SampleNavigationFrames(nint hwnd, [In] PointInt32[] points, int count,
        [Out] float[] rgb, [Out] long[] times, int maximumFrames, out int frames, nint ready, nint completed);

    private static async Task CheckPlaybackNavigationEntranceAsync(Window window, Grid root, ulong displayId,
        double white, double peak)
    {
        const int frameLimit = 96;
        bool animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        root.Background = new SolidColorBrush(Colors.Black);
        var previousPage = new Border { Background = new SolidColorBrush(Colors.Blue) };
        var frame = new Frame { Visibility = Visibility.Collapsed };
        root.Children.Add(previousPage);
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
        int hdrDraws = 0;
        bool prematureReveal = false;
        long hdrReadyAt = 0;
        long slideStartedAt = 0;
        double revealMilliseconds = 0;
        double slideMilliseconds = 0;
        double maxUiGapMilliseconds = 0;
        long lastUiTick = Stopwatch.GetTimestamp();
        var failure = new InvalidOperationException("Injected navigation first-frame failure.");
        page.ConfigureBackgroundCanvas = canvas =>
        {
            canvas.DrawHdrForeground += DrawMarker;
            canvas.HdrPresentationChanged += (active, _) =>
            {
                if (active)
                    hdrReadyAt = Stopwatch.GetTimestamp();
            };
            canvas.ApplyTemplate();
            var sdr = FindChild<CanvasAnimatedControl>(canvas, "PART_Canvas") ?? throw new Exception("Missing navigation SDR canvas.");
            sdr.Draw += (_, e) => e.DrawingSession.Clear(Colors.Red);
        };
        using var services = new ServiceCollection().AddSingleton(page).BuildServiceProvider();
        var navigation = new NavigationService(services, NullLogger<NavigationService>.Instance);
        navigation.Initialize(frame);
        navigation.RegisterPage<PlayingDetailPage>();
        long opacityToken = frame.RegisterPropertyChangedCallback(UIElement.OpacityProperty, (_, _) =>
        {
            if (navigation.IsPreparingShow && frame.Opacity == 1 && frame.RenderTransform is TranslateTransform)
                slideStartedAt = Stopwatch.GetTimestamp();
        });
        var uiTimer = root.DispatcherQueue.CreateTimer();
        uiTimer.Interval = TimeSpan.FromMilliseconds(20);
        uiTimer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            maxUiGapMilliseconds = Math.Max(maxUiGapMilliseconds, Stopwatch.GetElapsedTime(lastUiTick, now).TotalMilliseconds);
            lastUiTick = now;
        };
        uiTimer.Start();
        window.Activate();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 5);
        await WaitUntilAsync(() => root.ActualWidth > 0, "navigation fixture arrangement");
        var scale = GetDpiForWindow(hwnd) / 96d;
        var points = new[] { At(42), At(112), At(220) };
        PointInt32 At(int y) => new((int)(280 * scale), (int)(y * scale));
        try
        {
            if (Environment.GetEnvironmentVariable("PLAYBACK_HDR_PROBE_TIMING_WITHOUT_CAPTURE") == "1")
            {
                await MeasureNavigationAsync("Cold HDR navigation without capture", Show, requireResponsive: true);
                await DismissAsync();
                await MeasureNavigationAsync("Warm HDR navigation without capture", Show, requireResponsive: true);
                await DismissAsync();

                var delayedCanvas = await BeginDelayedHdrNavigationAsync();
                await MeasureNavigationAsync("Delayed HDR navigation without capture",
                    () => delayedCanvas.SetWindowPaused(false), requireResponsive: true);
                Console.WriteLine("PASS cold, warm, and delayed HDR navigation responsiveness without capture");
                return;
            }

            await CheckFirstVisibleFrameAsync("HDR navigation", Show);
            await DismissAsync();

            var canvas = await BeginDelayedHdrNavigationAsync();
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
            await VerifySdrFallbackAsync(4);
            await DismissAsync();
            failFirstDraw = false;
            await CheckFirstVisibleFrameAsync("HDR navigation after cancel and failure", Show);
            await DismissAsync();
            await CheckFirstVisibleFrameAsync("HDR navigation with motion disabled", () => ShowWithDuration(0),
                expectMotion: false);

            if (animationsEnabled)
            {
                await DismissAsync();
                Show();
                await WaitForSlideAsync();
                int priorReveals = revealCount;
                int priorCompletions = completionCount;
                model.IsPlayingDetailVisible = false;
                navigation.Dismiss();
                await Task.Delay(350);
                if (navigation.IsPreparingShow || frame.Visibility != Visibility.Collapsed || frame.Opacity != 1
                    || previousPage.Visibility != Visibility.Visible || revealCount != priorReveals
                    || completionCount != priorCompletions + 1)
                    throw new InvalidOperationException("Canceling an active HDR slide revealed the page or repeated its completion.");

                Show();
                await WaitForSlideAsync();
                int heldDraws = hdrDraws;
                await Task.Delay(65);
                if (!navigation.IsPreparingShow || hdrDraws != heldDraws)
                    throw new InvalidOperationException("Win2D redrew the scene while the compositor moved the committed HDR frame.");

                bool hidDisabledHdr = false;
                canvas.HdrPresentationChanged += ObserveMotionInterrupted;
                try
                {
                    priorCompletions = completionCount;
                    model.PlaybackHdrEnabled = false;
                    await VerifySdrFallbackAsync(priorCompletions + 1, requireReportedFailure: false);
                    if (!hidDisabledHdr)
                        throw new InvalidOperationException("Turning HDR off during the slide could expose SDR before fallback.");
                }
                finally
                {
                    canvas.HdrPresentationChanged -= ObserveMotionInterrupted;
                }

                await DismissAsync();
                model.PlaybackHdrEnabled = true;
                Show();
                await WaitForSlideAsync();
                var hdrPanel = FindChild<CanvasSwapChainPanel>(canvas, "PART_HdrPanel")
                    ?? throw new InvalidOperationException("The HDR panel did not load.");
                var oldChain = hdrPanel.SwapChain ?? throw new InvalidOperationException("The HDR chain was not committed.");
                var originalSize = window.AppWindow.Size;
                try
                {
                    window.AppWindow.Resize(new SizeInt32(originalSize.Width + 48, originalSize.Height + 24));
                    await WaitUntilAsync(() => hdrPanel.SwapChain is { } chain && !ReferenceEquals(chain, oldChain),
                        "a complete resize frame during the HDR slide");
                    if (!navigation.IsPreparingShow)
                        throw new InvalidOperationException("The HDR chain was not resized until the slide ended.");
                    await WaitUntilAsync(() => !navigation.IsPreparingShow && model.PlaybackHdrActive,
                        "HDR slide after resize");
                }
                finally
                {
                    window.AppWindow.Resize(originalSize);
                }

                await DismissAsync();
                priorCompletions = completionCount;
                Show();
                await WaitForSlideAsync();
                heldDraws = hdrDraws;
                await WaitUntilAsync(() => completionCount > priorCompletions && hdrDraws > heldDraws,
                    "HDR drawing resumed after the slide");
                failFirstDraw = true;
                await VerifySdrFallbackAsync(completionCount);
                Console.WriteLine("PASS HDR draw held during motion, size replacement, disable during slide, and failure after resuming");

                void ObserveMotionInterrupted(bool active, bool failed)
                {
                    if (!active)
                        hidDisabledHdr = frame.Opacity == 0 && previousPage.Visibility == Visibility.Visible;
                }
            }

            failFirstDraw = false;
            int negativeExitCallbacks = 0;
            await DismissAsync(-80, () => negativeExitCallbacks++);
            await CheckFirstVisibleFrameAsync("HDR navigation after negative dismissal duration", () => ShowWithDuration(0),
                expectMotion: false);
            int throwingExitCallbacks = 0;
            await DismissAsync(80, () =>
            {
                throwingExitCallbacks++;
                throw failure;
            });
            await Task.Delay(120);
            if (negativeExitCallbacks != 1 || throwingExitCallbacks != 1)
                throw new InvalidOperationException("Navigation did not notify exactly once after dismissal errors.");
            await CheckFirstVisibleFrameAsync("HDR navigation after a throwing exit callback", () => ShowWithDuration(0),
                expectMotion: false);
        }
        finally
        {
            uiTimer.Stop();
            frame.UnregisterPropertyChangedCallback(UIElement.OpacityProperty, opacityToken);
            frame.Content = null;
            root.Children.Remove(frame);
            root.Children.Remove(previousPage);
            marker?.Dispose();
        }
        Console.WriteLine("PASS production navigation reveal, delayed readiness, cancellation, fallback and re-entry");

        void DrawMarker(CanvasDrawingSession session, float _)
        {
            if (failFirstDraw) throw failure;
            hdrDraws++;
            if (!ReferenceEquals(markerDevice, session.Device))
            {
                marker?.Dispose();
                markerDevice = session.Device;
                marker = CanvasSolidColorBrush.CreateHdr(session.Device, new Vector4(0, 2.5f, 0, 1));
            }
            session.FillRectangle(255, 0, 65, (float)(page.BackgroundCanvas?.ActualHeight ?? 0), marker);
        }

        void Show() => ShowWithDuration(300);

        void ShowWithDuration(int duration)
        {
            previousPage.Visibility = Visibility.Visible;
            model.IsPlayingDetailVisible = true;
            slideStartedAt = 0;
            navigation.Show(typeof(PlayingDetailPage), duration, onCompleted: () => completionCount++, onRevealing: () =>
            {
                revealCount++;
                bool animated = frame.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue;
                if (model.PlaybackHdrActive && hdrReadyAt != 0)
                {
                    revealMilliseconds = Stopwatch.GetElapsedTime(hdrReadyAt).TotalMilliseconds;
                    slideMilliseconds = slideStartedAt == 0 ? 0 : Stopwatch.GetElapsedTime(slideStartedAt).TotalMilliseconds;
                }
                Console.WriteLine($"Navigation reveal opacity={frame.Opacity}, HDR={page.BackgroundCanvas?.IsHdrFramePresented}, cover preview={page.Cover.Opacity}, animated={animated}");
                prematureReveal |= model.PlaybackHdrEnabled && !failFirstDraw && (page.BackgroundCanvas?.IsHdrFramePresented != true
                    || page.Cover.Opacity != 0 || animated);
                previousPage.Visibility = Visibility.Collapsed;
            });
        }

        async Task DismissAsync(int duration = 80, Action? onCompleted = null)
        {
            bool closed = false;
            previousPage.Visibility = Visibility.Visible;
            model.IsPlayingDetailVisible = false;
            navigation.Dismiss(duration, () =>
            {
                closed = true;
                onCompleted?.Invoke();
            });
            await WaitUntilAsync(() => closed && frame.Visibility == Visibility.Collapsed && frame.Opacity == 1
                && frame.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue, "navigation dismissal");
        }

        Task WaitForSlideAsync() => WaitUntilAsync(() => navigation.IsPreparingShow && frame.Opacity == 1
            && frame.RenderTransform is TranslateTransform && previousPage.Visibility == Visibility.Visible,
            "active HDR slide");

        async Task VerifySdrFallbackAsync(int expectedCompletions, bool requireReportedFailure = true)
        {
            await WaitUntilAsync(() => completionCount == expectedCompletions && !model.PlaybackHdrActive
                && (!requireReportedFailure || model.PlaybackHdrFailed), "navigation fallback");
            await Task.Delay(80);
            var fallback = new float[3];
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, [points[1]], 1, fallback));
            if (model.PlaybackHdrActive || frame.Opacity != 1 || fallback[0] < .3f || fallback[1] > .1f || fallback[2] > .1f)
                throw new InvalidOperationException("The page did not expose a current SDR frame after a rendering failure.");
        }

        async Task<NowPlayingCanvas> BeginDelayedHdrNavigationAsync()
        {
            var canvas = page.BackgroundCanvas ?? throw new InvalidOperationException("The playback scene did not load.");
            int priorReveals = revealCount;
            int priorCompletions = completionCount;
            canvas.SetWindowPaused(true);
            Show();
            await Task.Delay(1450);
            if (!navigation.IsPreparingShow || frame.Opacity != 0 || frame.IsHitTestVisible
                || revealCount != priorReveals || completionCount != priorCompletions || model.PlaybackHdrActive)
                throw new InvalidOperationException("Slow HDR setup exposed an SDR frame or completed before a valid HDR frame.");
            return canvas;
        }

        async Task MeasureNavigationAsync(string label, Action start, bool requireResponsive = false)
        {
            int before = completionCount;
            using var monitoring = new CancellationTokenSource();
            var token = monitoring.Token;
            var responses = Task.Run(() => MonitorMessagePumpAsync(token));
            maxUiGapMilliseconds = 0;
            long started = lastUiTick = Stopwatch.GetTimestamp();
            double elapsed;
            try
            {
                start();
                await WaitUntilAsync(() => completionCount > before && model.PlaybackHdrActive && frame.Opacity == 1,
                    "navigation HDR entrance");
                elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!requireResponsive)
                    await Task.Delay(450); // Keep WGC and the message monitor active after rendering resumes.
            }
            finally
            {
                monitoring.Cancel();
            }
            var (maxResponse, timedOut) = await responses;
            Console.WriteLine($"{label} action to completion={elapsed:F0} ms, motion completion={slideMilliseconds:F0} ms, "
                + $"ready to completion={revealMilliseconds:F0} ms, max UI timer gap={maxUiGapMilliseconds:F0} ms, "
                + $"max WM_NULL response={maxResponse:F0} ms, timeouts={timedOut}");
            if (timedOut > 0 || maxUiGapMilliseconds >= 500 || (requireResponsive ? elapsed : slideMilliseconds) >= 1500)
                throw new InvalidOperationException($"{label} exceeded the UI responsiveness limits.");
        }

        async Task<(double MaxResponse, int Timeouts)> MonitorMessagePumpAsync(CancellationToken token)
        {
            double maxResponse = 0;
            int timedOut = 0;
            while (!token.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                if (SendMessageTimeout(hwnd, 0, 0, 0, 2, 500, out _) == 0) timedOut++;
                maxResponse = Math.Max(maxResponse, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                try { await Task.Delay(20, token); }
                catch (OperationCanceledException) { break; }
            }
            return (maxResponse, timedOut);
        }

        async Task CheckFirstVisibleFrameAsync(string label, Action start, bool expectMotion = true)
        {
            var pixels = new float[frameLimit * points.Length * 3];
            var times = new long[frameLimit];
            using var ready = new ManualResetEvent(false);
            using var completed = new ManualResetEvent(false);
            var capture = Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(SampleNavigationFrames(hwnd, points, points.Length, pixels, times, frameLimit,
                    out int count, ready.SafeWaitHandle.DangerousGetHandle(), completed.SafeWaitHandle.DangerousGetHandle()));
                return count;
            });
            int count;
            try
            {
                await WaitUntilAsync(() => ready.WaitOne(0) || capture.IsCompleted, "WGC initial navigation frame");
                if (!ready.WaitOne(0))
                {
                    await capture;
                    throw new InvalidOperationException("WGC did not capture the previous page before navigation.");
                }
                await MeasureNavigationAsync(label, start);
                await Task.Delay(75);
            }
            finally
            {
                completed.Set();
                count = await capture;
            }
            int[] firstHdr = [-1, -1, -1];
            int mixed = 0, offColor = 0, regressions = 0;
            float minHdr = float.PositiveInfinity, maxHdr = 0;
            for (int i = 0; i < count; ++i)
            {
                int hdrPoints = 0;
                for (int j = 0; j < points.Length; ++j)
                {
                    int p = (i * points.Length + j) * 3;
                    var (r, g, b) = (pixels[p], pixels[p + 1], pixels[p + 2]);
                    if (g is >= 2.35f and <= 2.65f && r < .12f && b < .12f)
                    {
                        if (firstHdr[j] < 0) firstHdr[j] = i;
                        minHdr = Math.Min(minHdr, g);
                        maxHdr = Math.Max(maxHdr, g);
                        hdrPoints++;
                    }
                    else if (b > .2f && r < .12f && g < .12f)
                    {
                        if (firstHdr[j] >= 0) regressions++;
                    }
                    else
                    {
                        if (offColor++ == 0)
                            Console.WriteLine($"Unexpected frame={i}, point={j}, rgb=({r:F3},{g:F3},{b:F3})");
                    }
                }
                if (hdrPoints > 0 && hdrPoints < points.Length)
                    mixed++;
            }
            bool shouldSlide = expectMotion && animationsEnabled;
            double pixelTravel = firstHdr.All(i => i >= 0)
                ? TimeSpan.FromTicks(times[firstHdr[0]] - times[firstHdr[2]]).TotalMilliseconds : 0;
            Console.WriteLine($"{label} WGC frames={count}, HDR top/middle/bottom={string.Join('/', firstHdr)}, mixed={mixed}, HDR green={minHdr:F3}-{maxHdr:F3}, off-color={offColor}, regressions={regressions}, row travel={pixelTravel:F0} ms");
            if (firstHdr.Any(i => i <= 0) || offColor > 0 || regressions > 0 || prematureReveal)
                throw new InvalidOperationException("The playback entrance exposed an incomplete or dim HDR scene.");
            if (shouldSlide ? mixed < 2 || firstHdr[2] >= firstHdr[1] || firstHdr[1] >= firstHdr[0]
                : mixed != 0 || firstHdr[0] != firstHdr[2])
                throw new InvalidOperationException("The HDR playback slide had the wrong direction or did not match the animation setting.");
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
        if (Environment.GetEnvironmentVariable("PLAYBACK_HDR_PROBE_TIMING_WITHOUT_CAPTURE") == "1") return;

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
