using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using WinUIMusicPlayer.Helper;
using AnimatedWin2dControls.Controls.AnimatedTextBlock;

namespace PlaybackHdrProbe;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [STAThread]
    static void Main()
    {
        SetProcessDpiAwarenessContext(-4); // use physical desktop coordinates when sampling the monitor
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new ProbeApp();
        });
    }
}

public sealed partial class ProbeApp : Application
{
    public ProbeApp()
    {
        UnhandledException += (_, e) => Console.Error.WriteLine($"XAML unhandled: {e.Message}\n{e.Exception}");
        InitializeComponent();
    }
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern int OpenProbe(out nint handle, out nint window, out int advancedColor);
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern void CloseProbe(nint handle);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);
    private static readonly bool CaptureWindow = Environment.GetCommandLineArgs().Contains("--sample-window");

    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern nint ShowBackdrop(nint host);
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern void ChangeBackdrop(nint behind);
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern int SampleFrame(nint host, [MarshalAs(UnmanagedType.Bool)] bool windowOnly, [In] PointInt32[] coordinates, int count, [Out] float[] rgb);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(PointInt32 point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);


    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? window = null;
        try
        {
            VerifyPublishedControls();
            if (Environment.GetCommandLineArgs().Contains("--check-lyrics-parallax"))
            {
                window = new Window { Title = "Isolated playback lyric pointer check" };
                await CheckLyricsParallaxAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-lyrics-acrylic"))
            {
                window = new Window { Title = "Playback lyrics acrylic check" };
                await CheckLyricsAcrylicAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-playbar-cover"))
            {
                window = new Window { Title = "Playback bar cover check" };
                await CheckPlaybackBarCoverAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-missing-cover-motion"))
            {
                window = new Window { Title = "Playback missing cover motion check" };
                await CheckMissingCoverMotionAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-cover-motion"))
            {
                window = new Window { Title = "Playback cover motion check" };
                await CheckCoverMotionAsync(window);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-page-cover"))
            {
                window = new Window { Title = "Playback detail cover check" };
                await CheckPageCoverAsync(window);
                return;
            }
            bool preparationOnly = Environment.GetCommandLineArgs().Contains("--check-hdr-preparation");
            if (preparationOnly || Environment.GetCommandLineArgs().Contains("--check-hdr-handoff"))
            {
                var handoffRoot = new Grid();
                window = new Window { Content = handoffRoot, Title = "Playback HDR handoff check" };
                await CheckHdrHandoffAsync(window, handoffRoot, preparationOnly);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--check-hdr-memory"))
            {
                var memoryRoot = new Grid();
                window = new Window { Content = memoryRoot, Title = "Playback HDR memory check" };
                await CheckHdrMemoryAsync(window, memoryRoot);
                return;
            }
            BrightnessChecks.Run();
            var testCover = await CheckCoverAsync();
            var panel = new CanvasSwapChainPanel();
            var root = new Grid();
            root.Children.Add(panel);
            root.Children.Add(new TextBlock { Text = "SDR reference", Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Bottom });
            window = new Window { Content = root, Title = "Playback HDR luminance reference" };
            // Keep the reference on the same monitor as the native test. Only the opt-in visual run shows it.
            window.AppWindow.MoveAndResize(new RectInt32(80, 290, 450, 160));
            bool inspect = Environment.GetCommandLineArgs().Contains("--inspect");
            bool sample = CaptureWindow || inspect || Environment.GetCommandLineArgs().Contains("--sample");
            if (sample)
            {
                if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    presenter.IsAlwaysOnTop = true;
                window.Activate();
                var probeWindow = WinRT.Interop.WindowNative.GetWindowHandle(window);
                ShowWindow(probeWindow, 5); // Override the console host's SW_HIDE startup hint for display sampling.
                double scale = GetDpiForWindow(probeWindow) / 96d;
                var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
                int width = (int)(450 * scale + 24), height = (int)(220 * scale);
                var center = new PointInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
                window.AppWindow.MoveAndResize(new RectInt32(center.X, center.Y, width, height));
                if (!CaptureWindow)
                    await LocateUnobstructedDisplayWindowAsync(window.AppWindow, probeWindow, area, width, height);
            }
            var display = DisplayInformation.CreateForWindowId(window.AppWindow.Id).GetAdvancedColorInfo();
            Console.WriteLine($"Display: {display.CurrentAdvancedColorKind}, SDR white {display.SdrWhiteLevelInNits}, peak {display.MaxLuminanceInNits} nit");
            using var device = new CanvasDevice();
            using var chain = new CanvasSwapChain(device, 450, 160, 96, DirectXPixelFormat.R16G16B16A16Float, 2, CanvasAlphaMode.Ignore);
            try
            {
                panel.SwapChain = chain;
                DrawReferences(device, chain);
                if (sample && display.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange)
                {
                    await Task.Delay(350);
                    nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    double dpi = GetDpiForWindow(hwnd) / 96.0;
                    int y = (int)(50 * dpi);
                    var points = new[] { new PointInt32((int)(50 * dpi), y), new PointInt32((int)(220 * dpi), y), new PointInt32((int)(360 * dpi), y) };
                    if (!TrySample(hwnd, points, "Opaque panel", out var panelPixels))
                        throw new Exception("Could not sample the HDR monitor at the reference window");
                    for (int i = 0; i < panelPixels.Length; i++)
                        if (Math.Abs(panelPixels[i] - (i / 3 + 1) * 2.5f) > .12f)
                            throw new Exception($"Opaque HDR monitor reference mismatch at channel {i}");
                    Console.WriteLine("PASS opaque final output matches 200/400/600 nit scRGB reference values");
                }
                await CheckCompositionAsync(inspect, sample && !CaptureWindow);
                panel.SwapChain = null;
                await CheckPlayerSceneAsync(root, window, display, sample, testCover);
            }
            finally { panel.SwapChain = null; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            window?.Close();
            Exit();
        }
    }

    private static async Task LocateUnobstructedDisplayWindowAsync(Microsoft.UI.Windowing.AppWindow window,
        nint hwnd, RectInt32 area, int width, int height)
    {
        // Global display capture may only sample pixels belonging to our test window.
        int left = area.X + 32, right = area.X + area.Width - width - 32;
        int top = area.Y + 32, bottom = area.Y + area.Height - height - 32;
        PointInt32[] locations = [window.Position, new(right, bottom), new(left, bottom), new(right, top), new(left, top)];
        foreach (var position in locations)
        {
            window.Move(position);
            await Task.Delay(120);
            bool IsVisibleAt(int x, int y) => GetAncestor(WindowFromPoint(new PointInt32(position.X + x, position.Y + y)), 2) == hwnd;
            if (IsVisibleAt(48, 80) && IsVisibleAt(width - 48, 80) && IsVisibleAt(width / 2, height / 2)
                && IsVisibleAt(48, height - 32) && IsVisibleAt(width - 48, height - 32)) return;
        }
        throw new InvalidOperationException("The reference window is occluded at all five display sampling locations.");
    }

    private static void VerifyPublishedControls()
    {
        if (Environment.GetEnvironmentVariable("PLAYBACK_HDR_PROBE_CONTROLS_HASH") is { Length: > 0 } expected)
        {
            using var assembly = File.OpenRead(typeof(AnimatedWin2dControls.Controls.NowPlayingCanvas).Assembly.Location);
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(assembly));
            if (actual != expected) throw new InvalidOperationException("Published controls assembly hash mismatch");
            Console.WriteLine($"PASS loaded published controls SHA256 {actual}");
        }
    }

    private static async Task CheckPlayerSceneAsync(Grid root, Window window, DisplayAdvancedColorInfo display, bool sample, PlaybackSceneCover testCover)
    {
        root.Children.Clear();
        var host = new AnimatedWin2dControls.Controls.NowPlayingCanvas();
        bool active = false, failed = false;
        var injectedFailure = new InvalidOperationException("Injected presentation failure");
        int draws = 0;
        host.HdrPresentationChanged += (a, f) => { active = a; failed = f; };
        host.DrawHdrForeground += (_, _) => draws++;
        host.ExceptionOccurred += (_, error) =>
        {
            if (!ReferenceEquals(error, injectedFailure)) Console.Error.WriteLine(error);
        };
        root.Children.Add(host);
        window.Activate();
        try
        {
            if (display.CurrentAdvancedColorKind != DisplayAdvancedColorKind.HighDynamicRange)
            {
                Console.WriteLine("Product HDR switch skipped on SDR output");
                return;
            }
            double white = Math.Min(300, display.MaxLuminanceInNits);
            host.SetHdrPresentation(white, Math.Min(600, display.MaxLuminanceInNits), true, 0);
            await WaitUntilAsync(() => active || failed, "initial HDR frame");
            if (failed) throw new Exception("Product HDR presentation failed");
            // Exercise every production renderer and its frame/cached resource migration.
            for (int i = 0; i < 7; i++)
            {
                host.BackgroundShaderIndex = i;
                int before = draws;
                await WaitUntilAsync(() => draws > before + 1 || failed, $"background {i}");
                if (failed) throw new Exception($"Shader {i} failed to present");
            }
            host.SetHdrPresentation(0, 0, true, 0);
            await WaitUntilAsync(() => !active, "SDR switch");
            int stopped = draws;
            await Task.Delay(100);
            if (draws != stopped) throw new Exception("HDR frames continued after switching to SDR");
            host.SetHdrPresentation(white, 700, true, 0);
            await WaitUntilAsync(() => active || failed, "HDR recovery");
            if (failed) throw new Exception("HDR did not recover");
            if (sample)
            {
                await CheckCoverOnDisplayAsync(testCover, window, host, (float)white, (float)Math.Min(600, display.MaxLuminanceInNits));
                await CheckTitleAsync(root, window, host, white);
                if (Environment.GetCommandLineArgs().Contains("--move-displays"))
                {
                    await CheckMonitorMovesAsync(window, host, root, testCover);
                    host.SetHdrPresentation(white, 600, true, 0);
                    await WaitUntilAsync(() => active || failed, "restoring the initial output after monitor moves");
                }
            }
            host.SetWindowPaused(true);
            stopped = draws;
            await Task.Delay(120);
            if (draws != stopped) throw new Exception("HDR frames continued while hidden");
            Console.WriteLine($"PASS production NowPlayingCanvas, all seven backgrounds, SDR/HDR switch and window pause ({draws} frames)");
            if (sample)
            {
                var backdrop = new Border { Background = new SolidColorBrush(Colors.Lime) };
                root.Children.Insert(0, backdrop);
                void FailFrame(CanvasDrawingSession _, float __) => throw injectedFailure;
                host.DrawHdrForeground += FailFrame;
                try
                {
                    host.SetHdrPresentation(white, 600, false, 0);
                    host.SetWindowPaused(false);
                    await WaitUntilAsync(() => failed, "HDR fallback notification");
                    await Task.Delay(150);
                    nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    if (!TrySample(hwnd, [new(160, 80)], "Window image after presentation failure", out var fallback)
                        || fallback[1] < display.SdrWhiteLevelInNits / 80 - .12 || fallback[0] > .12 || fallback[2] > .12)
                        throw new Exception("The failed HDR-only scene covered the window background");
                }
                finally { host.DrawHdrForeground -= FailFrame; root.Children.Remove(backdrop); }
                failed = false;
                host.RetryHdrPresentation();
                await WaitUntilAsync(() => active || failed, "HDR retry after presentation failure");
                if (failed) throw new Exception("The scene did not recover from the injected failure");
                Console.WriteLine("PASS HDR-only failure exposed window background, then recovered on retry");
                host.IsActive = false;
                host.SetHdrPresentation(0, 0, false, 0);
                if (!CaptureWindow) await CheckAcrylicFallbackAsync(root, window);
            }
        }
        finally { host.Dispose(); root.Children.Clear(); }
    }

    private static async Task CheckCoverOnDisplayAsync(PlaybackSceneCover cover, Window window,
        AnimatedWin2dControls.Controls.NowPlayingCanvas host, float white, float peak)
    {
        var bounds = new Rect(20, 12, 240, 80);
        PlaybackSceneCoverDrawing? drawing = null;
        int frames = 0;
        void Draw(CanvasDrawingSession session, float framePeak)
        {
            drawing ??= new PlaybackSceneCoverDrawing(cover, session.Device);
            drawing.Draw(session, bounds, 8, white, framePeak);
            frames++;
        }
        host.SetHdrPresentation(white, peak, true, 0);
        host.DrawHdrForeground += Draw;
        try
        {
            await WaitUntilAsync(() => frames >= 4, "SDR album cover on the HDR swap chain");
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            double dpi = GetDpiForWindow(hwnd) / 96d;
            int y = (int)(52 * dpi);
            PointInt32[] points = [new((int)(60 * dpi), y), new((int)(140 * dpi), y), new((int)(220 * dpi), y)];
            if (!TrySample(hwnd, points, "JPEG on shader background (gray, peach, white)", out var pixels))
                throw new Exception("Could not sample JPEG album cover on the HDR display");
            var expected = MemoryMarshal.Cast<byte, Half>(DrawCover(cover, white, peak));
            for (int tile = 0; tile < 3; tile++)
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(pixels[3 * tile + c] - (float)expected[(4 + 8 * tile) * 4 + c]) > .12f)
                        throw new Exception("The composited album artwork lost its scene luminance");
        }
        finally
        {
            host.DrawHdrForeground -= Draw;
            drawing?.Dispose();
        }
    }

    private static async Task CheckTitleAsync(Grid root, Window window, AnimatedWin2dControls.Controls.NowPlayingCanvas host, double whiteNits)
    {
        static AnimatedTextDocument MakeTitle(string suffix) => new(
            new AnimatedTextParagraph("鈻堚枅鈻堚枅" + suffix, 40, Microsoft.UI.Text.FontWeights.Bold, 56));
        var title = new AnimatedTextBlock
        {
            Width = 180, Height = 60, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = new SolidColorBrush(Colors.White), Document = MakeTitle("A"),
            TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextFadeEffect()
        };
        // Locate the XAML placeholder elsewhere so a white reading here can only come from the scene.
        root.Children.Add(title);
        static void ClearFrame(CanvasDrawingSession ds, float _) => ds.Clear(Colors.Black);
        host.DrawHdrForeground += ClearFrame;
        host.SetHdrTitle(title, new Rect(24, 10, 180, 60));
        try
        {
            await Task.Delay(1250);
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            double scale = GetDpiForWindow(hwnd) / 96d;
            PointInt32[] points = [new((int)(45 * scale), (int)(37 * scale)), new((int)(56 * scale), (int)(37 * scale))];
            if (!TrySample(hwnd, points, "Idle scene title", out var idle) || Math.Abs(idle.Max() - whiteNits / 80) > .12)
                throw new Exception("Idle title did not match SDR reference white inside the HDR scene");
            title.Document = MakeTitle("B");
            await Task.Delay(140);
            if (!TrySample(hwnd, points, "Animated scene title", out var animated)
                || animated.Max() <= 0 || animated.Max() > idle.Max() + .08)
                throw new Exception("The title disappeared during its transition or exceeded reference white");
            await Task.Delay(1100);
            if (!TrySample(hwnd, points, "Settled scene title", out var settled) || Math.Abs(settled.Max() - idle.Max()) > .10f)
                throw new Exception("The settled title did not return to reference white");
            title.TextEffect = new AnimatedWin2dControls.Controls.AnimatedTextBlock.Effects.TextWipeEffect();
            title.Document = MakeTitle("C");
            await Task.Delay(370);
            if (!TrySample(hwnd, points, "Wipe title at reference white", out var wipe) || Math.Abs(wipe.Max() - idle.Max()) > .12)
                throw new Exception("An HDR title transition brightened the original white ink");
            Console.WriteLine("PASS HDR title preserves original fade, wipe and steady reference white");
        }
        finally
        {
            host.SetHdrTitle(null, default);
            host.DrawHdrForeground -= ClearFrame;
            root.Children.Remove(title);
        }
    }

    private static async Task CheckAcrylicFallbackAsync(Grid root, Window window)
    {
        var lyrics = new AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.AdvanceLyricsCanvasControl
            { UseTransparentPresentation = true };
        window.SystemBackdrop = new CustomAcrylicSystemBackdrop(window) { TintOpacity = 0, LuminosityOpacity = .4 };
        root.Children.Add(lyrics);
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        nint backing = ShowBackdrop(hwnd);
        try
        {
            if (backing == 0) throw new Exception("Missing test pattern");
            await Task.Delay(1000);
            PointInt32[] center = [new(230, 145)];
            PointInt32[] border = [new(6, 100)];
            bool measured = TrySample(hwnd, center, "Player acrylic red", out var red);
            measured &= TrySample(backing, border, "Reference red", out var redReference);
            ChangeBackdrop(backing);
            await Task.Delay(1000);
            measured &= TrySample(hwnd, center, "Player acrylic blue", out var blue);
            measured &= TrySample(backing, border, "Reference blue", out var blueReference);
            if (!measured) throw new Exception("Could not sample the acrylic test window or its backing pattern");
            bool referenceChanged = redReference[0] > blueReference[0] + .5f && blueReference[2] > redReference[2] + .5f;
            bool tracksDesktop = red[0] > blue[0] + .015f && blue[2] > red[2] + .015f;
            Console.WriteLine($"Player acrylic + SDR lyric host: referenceChanged={referenceChanged}, tracksDesktop={tracksDesktop}");
            if (!referenceChanged) throw new Exception("The acrylic test backing pattern did not change");
            bool expectAcrylic = new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled &&
                Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported() &&
                !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            if (expectAcrylic && !tracksDesktop) throw new Exception("The acrylic foreground blocked its backing pattern");
        }
        finally
        {
            if (backing != 0) DestroyWindow(backing);
            lyrics.PrepareForShutdown();
            root.Children.Clear();
            window.SystemBackdrop = null;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string operation)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && elapsed.ElapsedMilliseconds < 8000) await Task.Delay(25);
        if (!predicate()) throw new TimeoutException(operation);
    }

    private static async Task<PlaybackSceneCover> CheckCoverAsync()
    {
        await CoverPresenterChecks.RunAsync();
        // Four neutral RGBA16 samples: black, 200 nit, 600 nit, and 600 nit at half coverage.
        const string fixture = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAABEAYAAACprNOOAAAABGNJQ1AJEAABTSMj/gAAABtJREFUeJxjYACD//+nOILg//+bHEAQRjcwAADXFw6msE0u5gAAAABJRU5ErkJggg==";
        string path = Path.Combine(Path.GetTempPath(), $"playback-hdr-{Guid.NewGuid():N}.png");
        var bytes = Convert.FromBase64String(fixture);
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var cover = await PlaybackSceneCover.ReadAsync(path, false, default) ?? throw new Exception("PQ PNG was rejected");
            CheckPixels(cover.Pixels, [0, 2.5f, 7.5f, 3.75f]);
            if (Math.Abs(cover.PeakNits - 600) > 1 || cover.Width != 4 || cover.Height != 1)
                throw new Exception($"Unexpected dimensions or MaxCLL: {cover}");
            var nativeOutput = DrawCover(cover, 0, 400);
            CheckPixels(nativeOutput, [0, 2.5f, 5f, 2.5f]);
            if (Math.Abs((float)MemoryMarshal.Cast<byte, Half>(nativeOutput)[15] - .5f) > .001f)
                throw new Exception("Native tone mapping changed artwork coverage");
            bytes[42] = 13; // change the transfer function from PQ to sRGB
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(45), 0x59dd13ddu);
            await File.WriteAllBytesAsync(path, bytes);
            if (await PlaybackSceneCover.ReadAsync(path, false, default) is not null) throw new Exception("SDR PNG accepted as native HDR");
            const string sdrFixture = "iVBORw0KGgoAAAANSUhEUgAAAAYAAAABCAYAAAD9yd/wAAAAGklEQVR4nGNgYGD4X1ZW9v/OnTv/oaABiBkAvhARaqhotO8AAAAASUVORK5CYII=";
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(sdrFixture));
            var ordinary = await PlaybackSceneCover.ReadAsync(path, true, default) ?? throw new Exception("Ordinary album cover did not enter the HDR scene");
            CheckPixels(DrawCover(ordinary, 200, 600), [0, .498f, 2.057f, 3.362f, 1.687f, 0]);
            CheckPixels(DrawCover(ordinary, 400, 600), [0, .997f, 4.069f, 6.416f, 3.221f, 0]);
            CheckPixels(DrawCover(ordinary, 200, 1000), [0, .498f, 2.063f, 3.397f, 1.705f, 0]);
            CheckPixels(DrawCover(ordinary, 200, 200), [0, .498f, 1.932f, 2.5f, 1.255f, 0]);
            CheckPixels(DrawCover(ordinary, 80, 4000), [0, .199f, .827f, 1.374f, .690f, 0]);
            CheckCoverRamp();
            // Three 8x8 JPEG tiles: neutral gray, warm peach and white. Raw music caches can have any extension.
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(CoverPresenterChecks.JpegFixture));
            var jpegCover = await PlaybackSceneCover.ReadAsync(path, true, default) ?? throw new Exception("Missing JPEG scene artwork");
            var jpegPixels = DrawCover(jpegCover, 200, 600);
            var warm = MemoryMarshal.Cast<byte, Half>(jpegPixels).Slice(12 * 4, 3);
            if (Math.Abs((float)MemoryMarshal.Cast<byte, Half>(jpegPixels)[0] - .498f) > .015f ||
                (float)warm[0] <= 2.5f || (float)warm[0] <= (float)warm[1] * 1.3f || (float)warm[1] <= (float)warm[2] * 1.3f)
                throw new Exception("SDR JPEG lost its midtones, hue or highlight headroom");
            CheckArtworkScale(jpegCover, jpegPixels);
            Console.WriteLine("PASS native PQ, sRGB PNG/JPEG artwork, independent reference white/peak, hue and alpha in FP16");
            return jpegCover;
        }
        finally { File.Delete(path); }
    }

    private static void CheckCoverRamp()
    {
        byte[] pixels = new byte[256 * 4];
        for (int code = 0; code < 256; code++)
        {
            pixels.AsSpan(code * 4, 3).Fill((byte)code);
            pixels[code * 4 + 3] = 255;
        }
        var ramp = new PlaybackSceneCover(pixels, 256, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized, 0);
        foreach (var (peakNits, expectedWhite) in new[] { (600f, 396f), (310f, 310f), (300f, 300f) })
        {
            var output = MemoryMarshal.Cast<byte, Half>(DrawCover(ramp, 300, peakNits));
            float lastNits = 0;
            for (int code = 1; code < 256; code++)
            {
                float nits = (float)output[code * 4] * 80;
                if (nits <= lastNits || nits - lastNits > 5 || nits > peakNits + .2f)
                    throw new Exception($"Cover ramp lost its gradient or exceeded {peakNits} nit at sRGB code {code}");
                lastNits = nits;
            }
            float midGray = (float)output[128 * 4] * 80;
            if (Math.Abs(lastNits - expectedWhite) > .25f || Math.Abs(midGray - 71.23f) > .25f)
                throw new Exception($"Unexpected cover exposure: mid-gray {midGray} nit, white {lastNits} nit");
            Console.WriteLine($"PASS 256-step cover ramp at 300/{peakNits:F0}, mid-gray {midGray:F1} nit and white {lastNits:F1} nit");
        }

        // A fully saturated red channel must also respect the peak when luma alone is low.
        var colored = new PlaybackSceneCover([64, 128, 255, 255, 32, 64, 128, 128], 2, 1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 0);
        var rgba = MemoryMarshal.Cast<byte, Half>(DrawCover(colored, 300, 300));
        float[] opaque = [3.75f, .8095f, .1923f, 1f];
        for (int channel = 0; channel < 4; channel++)
        {
            if (Math.Abs((float)rgba[channel] - opaque[channel]) > .01f ||
                Math.Abs((float)rgba[channel + 4] - opaque[channel] * (128f / 255)) > .01f)
                throw new Exception("The cover shoulder changed saturated RGB ratios, alpha, or the peak.");
        }
    }

    private static void CheckArtworkScale(PlaybackSceneCover cover, byte[] reference)
    {
        using var device = new CanvasDevice();
        using var drawing = new PlaybackSceneCoverDrawing(cover, device);
        foreach (int dpi in new[] { 96, 144 })
        foreach (float scale in new[] { .5f, 10f })
        {
            using var target = new CanvasRenderTarget(device, cover.Width * scale, cover.Height * scale, dpi,
                DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Ignore);
            using (var session = target.CreateDrawingSession())
            {
                session.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                session.Clear(Colors.Black);
                drawing.Draw(session, target.Bounds, scale, 200, 600);
            }
            var scaled = MemoryMarshal.Cast<byte, Half>(target.GetPixelBytes());
            var original = MemoryMarshal.Cast<byte, Half>(reference);
            int row = (int)(target.SizeInPixels.Height / 2 * target.SizeInPixels.Width);
            for (int tile = 0; tile < 3; tile++)
            {
                int x = (int)((4 + 8 * tile) * scale * dpi / 96);
                for (int c = 0; c < 3; c++)
                    if (Math.Abs((float)scaled[(row + x) * 4 + c] - (float)original[(4 + 8 * tile) * 4 + c]) > .03f)
                        throw new Exception($"Scaled {dpi} dpi album tile {tile} lost its original luminance");
            }
        }
        Console.WriteLine("PASS scaled artwork at 0.5x and 10x with rounded corners and 96/144 DPI");
    }

    private static byte[] DrawCover(PlaybackSceneCover cover, float whiteNits, float peakNits)
    {
        using var device = new CanvasDevice();
        using var drawing = new PlaybackSceneCoverDrawing(cover, device);
        using var target = new CanvasRenderTarget(device, cover.Width, cover.Height, 96,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            drawing.Draw(session, target.Bounds, 0, whiteNits, peakNits);
        }
        return target.GetPixelBytes();
    }

    private static void CheckPixels(byte[] data, float[] expected)
    {
        var half = MemoryMarshal.Cast<byte, Half>(data);
        for (int i = 0; i < expected.Length; i++)
            for (int c = 0; c < 3; c++)
                if (Math.Abs((float)half[i * 4 + c] - expected[i]) > .03)
                    throw new Exception($"Pixel {i}, channel {c}: got {half[i * 4 + c]}, expected {expected[i]}");
    }

    private static void DrawReferences(CanvasDevice device, CanvasSwapChain chain)
    {
        using var rt = new CanvasRenderTarget(device, 3, 1, 96, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        float[] levels = [2.5f, 5f, 7.5f];
        using (var target = rt.CreateDrawingSession())
        {
            for (int i = 0; i < 3; i++)
            {
                using var brush = CanvasSolidColorBrush.CreateHdr(device, new Vector4(levels[i], levels[i], levels[i], 1));
                target.FillRectangle(i, 0, 1, 1, brush);
            }
        }
        CheckPixels(rt.GetPixelBytes(), levels);
        using (var session = chain.CreateDrawingSession(Colors.Black))
            session.DrawImage(rt, new Rect(0, 0, 450, 105), new Rect(0, 0, 3, 1), 1, CanvasImageInterpolation.NearestNeighbor);
        chain.Present();
        Console.WriteLine("PASS FP16 Win2D target readback 2.5/5/7.5, panel attached and presented");
    }

    private static bool TrySample(nint hwnd, PointInt32[] points, string label, out float[] rgb)
    {
        rgb = new float[3 * points.Length];
        int hr = SampleFrame(hwnd, CaptureWindow, points, points.Length, rgb);
        if (hr < 0)
        {
            Console.WriteLine($"{label} final display sampling unavailable: 0x{hr:X8}");
            return false;
        }
        Console.WriteLine($"{label} scRGB: {string.Join(", ", rgb.Select(x => x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}");
        return true;
    }

    private static bool Increasing(float[] rgb, int i) =>
        rgb[i] > 1 && rgb[i + 3] > rgb[i] + 1 && rgb[i + 6] > rgb[i + 3] + 1;

    private static async Task CheckCompositionAsync(bool inspect, bool sample)
    {
        int hr = OpenProbe(out nint handle, out nint hwnd, out int hdr);
        if (hr < 0)
        {
            Console.WriteLine($"Acrylic composition unavailable: 0x{hr:X8}. Product fallback: SDR foreground.");
            return;
        }
        nint behind = 0;
        try
        {
            using var island = new DesktopWindowXamlSource();
            island.Initialize(Win32Interop.GetWindowIdFromWindow(hwnd));
            var button = new Button { Content = "Flyout / focus", Flyout = new Flyout { Content = new TextBlock { Text = "XAML overlay" } } };
            var overlay = new Grid();
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.VerticalAlignment = VerticalAlignment.Top;
            overlay.Children.Add(button);
            overlay.Children.Add(new Border { Width = 40, Height = 18, Background = new SolidColorBrush(Colors.Lime),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(170, 0, 0, 0) });
            island.Content = overlay;
            island.SiteBridge.MoveAndResize(new RectInt32(0, 0, 450, 180));
            island.SiteBridge.Show();
            if (sample)
            {
                behind = ShowBackdrop(hwnd);
                if (behind == 0) throw new Exception("Could not create the controlled backdrop");
                await Task.Delay(750);
                double scale = GetDpiForWindow(hwnd) / 96d;
                PointInt32[] points = [new(45, 115), new(115, 115), new(195, 115), new(275, 115), new(355, 115),
                    new((int)(185 * scale), (int)(8 * scale))];
                bool gotRed = TrySample(hwnd, points, "Red backdrop", out var red);
                ChangeBackdrop(behind);
                await Task.Delay(750);
                bool gotBlue = TrySample(hwnd, points, "Blue backdrop", out var blue);
                if (gotRed && gotBlue)
                {
                    bool transparent = red[0] - blue[0] > .015f && blue[2] - red[2] > .015f;
                    bool half = red[12] - blue[12] > .006f && blue[14] - red[14] > .006f;
                    bool swatch = red[16] > red[15] + .4f && red[16] > red[17] + .4f;
                    bool halfCorrect = Math.Abs(red[12] - (.5f * red[9] + .5f * red[0])) < .1f;
                    Console.WriteLine($"Native final output: desktopVisible={transparent}, halfAlphaCorrect={halfCorrect}, backdropChangesAtHalf={half}, HDRSteps={Increasing(red, 3)}, XamlAbove={swatch}");
                }
                button.Flyout.ShowAt(button);
                await Task.Delay(300);
                Console.WriteLine($"XAML flyout opened: {button.Flyout.IsOpen}");
                if (inspect) await Task.Delay(30000);
            }
            Console.WriteLine($"PASS native desktop backdrop + FP16 premultiplied swap chain + XAML island attached; HDR output reported: {hdr != 0}.");
            if (!sample) Console.WriteLine("Final desktop composition was not sampled in this run.");
        }
        finally
        {
            CloseProbe(handle);
            if (behind != 0) DestroyWindow(behind);
        }
    }
}
