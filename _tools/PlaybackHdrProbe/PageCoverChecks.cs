using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Display;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    private static async Task CheckPageCoverAsync(Window window)
    {
        DisplayArea? output = null;
        double peak = 0;
        var areas = DisplayArea.FindAll();
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
        if (output is null) throw new InvalidOperationException("The detail cover check requires an HDR output.");

        // Neutral 32x20 SDR artwork with a white left half and 160/255 gray right half.
        const string coverPng = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAUCAIAAABj86gYAAAAIUlEQVR4nGP4TyJYQCJgGLVg1IJRC0YtGLVg1IJRC4gBAIOZFJvNh2SfAAAAAElFTkSuQmCC";
        const string backgroundPng = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFUlEQVR4nGNgYNAgEY1qGNUwbDUAAB6jKAFBrRkwAAAAAElFTkSuQmCC";
        string coverPath = Path.Combine(Path.GetTempPath(), $"detail-cover-{Guid.NewGuid():N}.png");
        string backgroundPath = Path.Combine(Path.GetTempPath(), $"detail-background-{Guid.NewGuid():N}.png");
        string cachedOnly = Path.Combine(Path.GetTempPath(), $"detail-cached-cover-{Guid.NewGuid():N}.png");
        string displayCache = PlaybackCoverImage.GetCachePath(cachedOnly);
        App.MainWindow = window;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        double white = Math.Min(200, peak);
        try
        {
            await File.WriteAllBytesAsync(coverPath, Convert.FromBase64String(coverPng));
            await File.WriteAllBytesAsync(backgroundPath, Convert.FromBase64String(backgroundPng));
            Position(830, 530);
            window.Activate();
            ShowWindow(hwnd, 5);
            var imageHdr = await CheckEntryAsync(false, false);
            Position(530, 790);
            var portraitImageHdr = await CheckEntryAsync(false, true);
            Position(830, 530);
            var shaderHdr = await CheckEntryAsync(true, false);
            await CheckShaderSdrEntryAsync();
            if (imageHdr.Zip(shaderHdr).Any(pair => Math.Abs(pair.First - pair.Second) > .08f) ||
                imageHdr.Zip(portraitImageHdr).Any(pair => Math.Abs(pair.First - pair.Second) > .08f))
                throw new InvalidOperationException("Changing layout or background changed the HDR album-cover pixels.");
            File.Copy(coverPath, displayCache);
            await CheckEntryAsync(false, false, cachedOnly);
            await CheckPortraitResizeAsync();
            await CheckReleasedCoverBitmapsAsync(window, hwnd);
            Console.WriteLine("PASS detail cover on image and shader backgrounds, first layout in both orientations, cached-only XAML fallback and portrait resizing");
        }
        finally
        {
            File.Delete(coverPath);
            File.Delete(backgroundPath);
            File.Delete(displayCache);
        }

        void Position(int width, int height)
        {
            var work = output.WorkArea;
            window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2, width, height));
        }

        async Task<float[]> CheckEntryAsync(bool shader, bool portrait, string? artworkPath = null)
        {
            string label = $"{(shader ? "shader" : "custom image")}, {(portrait ? "portrait" : "landscape")}{(artworkPath is null ? "" : " cached-only")}";
            artworkPath ??= coverPath;
            var vm = new AppViewModel
            {
                IsFluidBackgroundEnabled = shader,
                WindowBackgroundImagePath = shader ? null : backgroundPath,
                LyricPageBackgroundHash = artworkPath,
                PlaybackHdrEnabled = true,
                PlaybackHdrWhiteNits = white,
                PlaybackHdrEffectivePeakNits = peak,
                PlaybackHdrDisplayId = output.DisplayId.Value
            };
            using var page = new PlayingDetailPage(vm);
            (Rect Slot, bool Portrait)? first = null;
            int previewReveals = 0;
            long observer = page.Cover.RegisterPropertyChangedCallback(UIElement.OpacityProperty, (_, _) =>
            {
                if (page.Cover.Opacity == 0) first ??= (page.CoverSlot, vm.IsPortraitLayout);
                else if (first.HasValue) previewReveals++;
            });
            window.Content = page;
            try
            {
                await WaitUntilAsync(() => vm.PlaybackHdrFailed || (first.HasValue && vm.PlaybackHdrActive), $"HDR first frame: {label}");
                if (vm.PlaybackHdrFailed) throw new InvalidOperationException($"The HDR page failed to present: {label}.");
                var initial = first!.Value;
                if (initial.Portrait != portrait || initial.Slot.Width < 100 || initial.Slot.Height < 100)
                    throw new InvalidOperationException($"HDR replaced the preview before the first layout: {label}, slot {initial.Slot}.");
                var canvas = page.BackgroundCanvas ?? throw new InvalidOperationException("Missing bound NowPlaying canvas.");
                if (canvas.UseImageBackground == shader)
                    throw new InvalidOperationException($"Production background binding chose the wrong renderer: {label}.");
                var panel = FindChild<CanvasSwapChainPanel>(canvas, "PART_HdrPanel") ?? throw new InvalidOperationException("Missing HDR panel.");
                if (panel.SwapChain is null) throw new InvalidOperationException($"HDR committed without a swap chain: {label}.");

                Rect art = Fit(initial.Slot);
                await Task.Delay(100);
                var hdrPixels = SampleArt(art, $"{label} HDR");
                double whiteScRgb = white / 80;
                double headroom = Math.Max(peak / 80 - whiteScRgb, 0);
                double liftLimit = .25 * whiteScRgb;
                double expectedWhite = Math.Min(peak / 80, 1.10 * (whiteScRgb + liftLimit * headroom / (liftLimit + headroom)));
                double expectedGray = Math.Pow((160.0 / 255 + .055) / 1.055, 2.4) * 1.10 * whiteScRgb;
                CheckSamples(hdrPixels, expectedWhite, expectedGray, label);
                await Task.Delay(300);
                AssertStable(initial.Slot, page.CoverSlot, label);

                vm.PlaybackHdrEnabled = false;
                await WaitUntilAsync(() => !vm.PlaybackHdrActive && panel.SwapChain is null &&
                    (shader ? page.Cover.Opacity == 0 : page.Cover.Opacity == 1 && page.Cover.IsCoverSettled(artworkPath)),
                    $"SDR cover after turning off HDR: {label}");
                Rect sdrArt = shader ? Fit(page.CoverSlot) : page.Cover.GetArtworkBounds(page);
                AssertStable(art, sdrArt, $"{label}, HDR to SDR");
                await Task.Delay(130);
                var sdrPixels = SampleArt(sdrArt, $"{label} SDR after disabling HDR");
                AssertSdrArtwork(sdrPixels, label);

                if (shader)
                {
                    vm.PlaybackHdrEnabled = true;
                    await WaitUntilAsync(() => vm.PlaybackHdrActive && panel.SwapChain is not null, "shader HDR re-enabled");
                    if (previewReveals != 0 || page.Cover.Opacity != 0)
                        throw new InvalidOperationException("Switching shader output between SDR and HDR revealed the XAML cover.");
                    CheckSamples(SampleArt(art, "shader HDR after SDR"), expectedWhite, expectedGray, label);
                }
                else if (!portrait)
                {
                    vm.PlaybackHdrEnabled = true;
                    await WaitUntilAsync(() => vm.PlaybackHdrActive && page.Cover.Opacity == 0, "HDR re-enabled");
                    window.SystemBackdrop = new CustomAcrylicSystemBackdrop(window) { TintOpacity = 0, LuminosityOpacity = .4 };
                    vm.WindowBackgroundImagePath = null;
                    await WaitUntilAsync(() => page.ViewModel.UseAcrylicLyricsPresentation && !vm.PlaybackHdrActive &&
                        panel.SwapChain is null && page.Cover.Opacity == 1, "XAML preview on acrylic with HDR setting enabled");
                    await Task.Delay(130);
                    AssertSdrArtwork(SampleArt(sdrArt, "transparent acrylic fallback"), "acrylic");
                }
                return hdrPixels;
            }
            finally
            {
                page.Cover.UnregisterPropertyChangedCallback(UIElement.OpacityProperty, observer);
                window.SystemBackdrop = null;
                window.Content = new Grid();
            }
        }

        async Task CheckShaderSdrEntryAsync()
        {
            var vm = new AppViewModel
            {
                IsFluidBackgroundEnabled = true,
                WindowBackgroundImagePath = backgroundPath,
                LyricPageBackgroundHash = coverPath
            };
            using var page = new PlayingDetailPage(vm);
            window.Content = page;
            try
            {
                await WaitUntilAsync(() => page.Cover.Opacity == 0, "cold SDR shader scene cover");
                var canvas = page.BackgroundCanvas ?? throw new InvalidOperationException("Missing SDR shader canvas.");
                var panel = FindChild<CanvasSwapChainPanel>(canvas, "PART_HdrPanel");
                if (panel?.SwapChain is not null || vm.PlaybackHdrActive)
                    throw new InvalidOperationException("SDR shader cover unexpectedly used the HDR swap chain.");

                Rect art = Fit(page.CoverSlot);
                await Task.Delay(130);
                AssertSdrArtwork(SampleArt(art, "cold SDR shader scene"), "cold SDR");
                vm.IsFluidBackgroundEnabled = false;
                await WaitUntilAsync(() => canvas.UseImageBackground && page.Cover.Opacity == 1 &&
                    page.Cover.IsCoverSettled(coverPath), "SDR custom image restores the XAML cover");
                AssertStable(art, page.Cover.GetArtworkBounds(page), "SDR shader to XAML");
                vm.IsFluidBackgroundEnabled = true;
                await WaitUntilAsync(() => !canvas.UseImageBackground && page.Cover.Opacity == 0, "SDR shader reclaims its cover");
            }
            finally { window.Content = new Grid(); }
        }

        async Task CheckPortraitResizeAsync()
        {
            // The Canvas measures an explicit page viewport even when its bottom is outside the test window.
            // This reaches the portrait text/cover breakpoint on both short and tall test displays.
            var vm = new AppViewModel { LyricPageBackgroundHash = coverPath };
            using var page = new PlayingDetailPage(vm) { Width = 1000, Height = 1320 };
            var host = new Canvas();
            host.Children.Add(page);
            window.Content = host;
            try
            {
                await WaitUntilAsync(() => vm.IsPortraitLayout && Math.Abs(page.Cover.ActualWidth - 220) < 1,
                    "initial portrait slot below the 1024 DIP breakpoint");
                page.Width = 1080;
                await WaitUntilAsync(() => Math.Abs(page.ActualWidth - 1080) < 1, "portrait viewport resized");
                if (!vm.IsPortraitLayout || Math.Abs(page.Cover.ActualWidth - 240) >= 1)
                    throw new InvalidOperationException($"Portrait cover width after crossing 1024 DIP was {page.Cover.ActualWidth:F1}, expected 240.");
                Console.WriteLine("PASS portrait layout crosses the 1024 DIP breakpoint with cover slot changing from 220 to 240 DIP");
            }
            finally { window.Content = new Grid(); }
        }

        float[] SampleArt(Rect art, string label)
        {
            double scale = GetDpiForWindow(hwnd) / 96d;
            int y = (int)((art.Y + art.Height * .5) * scale);
            PointInt32[] points = [new((int)((art.X + art.Width * .17) * scale), y), new((int)((art.X + art.Width * .83) * scale), y)];
            var pixels = new float[points.Length * 3];
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, points, points.Length, pixels));
            Console.WriteLine($"{label}, {GetDpiForWindow(hwnd)} DPI, {art.X:F1}/{art.Y:F1} {art.Width:F1}x{art.Height:F1}, scRGB {string.Join(", ", pixels.Select(p => p.ToString("F3")))}");
            return pixels;
        }
    }

    private static async Task CheckReleasedCoverBitmapsAsync(Window window, nint hwnd)
    {
        byte[] jpeg = Convert.FromBase64String(CoverPresenterChecks.JpegFixture);
        string path = Path.Combine(Path.GetTempPath(), $"detail-jpeg-{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(path, jpeg);
            using var fromFile = await ImageHelper.DecodeFileToBitmapAsync(path, default) as SoftwareBitmapSource
                ?? throw new InvalidOperationException("The JPEG file did not decode into a XAML source.");
            using var fromBytes = await ImageHelper.DecodeToBitmapAsync(jpeg) as SoftwareBitmapSource
                ?? throw new InvalidOperationException("The JPEG bytes did not decode into a XAML source.");
            using var placeholder = await ImageHelper.DecodeApplicationAssetAsync("default_cover_black.png", default) as SoftwareBitmapSource
                ?? throw new InvalidOperationException("The packaged placeholder did not decode into a XAML source.");
            var root = new Canvas { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) };
            Add(fromFile, 20, 20, 180, 60);
            Add(fromBytes, 20, 90, 180, 60);
            Add(placeholder, 220, 20, 120, 120);
            window.Content = root;
            await WaitUntilAsync(() => root.IsLoaded && root.ActualWidth > 340, "XAML artwork copy layout");
            await Task.Delay(130);
            double scale = root.XamlRoot.RasterizationScale;
            PointInt32[] points = [Sample(50, 50), Sample(110, 50), Sample(170, 50),
                Sample(50, 120), Sample(110, 120), Sample(170, 120), Sample(240, 40)];
            var pixels = new float[points.Length * 3];
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, points, points.Length, pixels));
            if (!pixels.Take(9).Zip(pixels.Skip(9).Take(9)).All(pair => Math.Abs(pair.First - pair.Second) < .025f) ||
                pixels[0] < .10f || pixels[6] < pixels[0] * 2 || pixels[3] <= pixels[4] * 1.32f ||
                pixels[18] / pixels[6] is < .006f or > .020f || Math.Abs(pixels[18] - pixels[19]) > .01f)
                throw new InvalidOperationException($"SoftwareBitmapSource did not retain the JPEG and placeholder pixels: {string.Join(", ", pixels)}.");
            Console.WriteLine($"PASS XAML rendered file/byte JPEG and themed PNG after input bitmaps were disposed, JPEG white {pixels[6]:F3}, PNG background {pixels[18]:F3} scRGB");

            void Add(SoftwareBitmapSource source, int left, int top, int width, int height)
            {
                var image = new Image { Source = source, Width = width, Height = height };
                Canvas.SetLeft(image, left);
                Canvas.SetTop(image, top);
                root.Children.Add(image);
            }

            PointInt32 Sample(int x, int y) => new((int)(x * scale), (int)(y * scale));
        }
        finally
        {
            window.Content = new Grid();
            File.Delete(path);
        }
    }

    private static Rect Fit(Rect slot)
    {
        double scale = Math.Min(slot.Width / 32, slot.Height / 20);
        double width = 32 * scale, height = 20 * scale;
        return new Rect(slot.X + (slot.Width - width) / 2, slot.Y + (slot.Height - height) / 2, width, height);
    }

    private static void AssertStable(Rect first, Rect current, string label)
    {
        if (Math.Abs(first.X - current.X) > 1.5 || Math.Abs(first.Y - current.Y) > 1.5 ||
            Math.Abs(first.Width - current.Width) > 1.5 || Math.Abs(first.Height - current.Height) > 1.5)
            throw new InvalidOperationException($"{label}: artwork moved or resized ({first} to {current}).");
    }

    private static void CheckSamples(float[] pixels, double white, double gray, string label)
    {
        if (pixels.Take(3).Any(value => Math.Abs(value - white) > .12) ||
            pixels.Skip(3).Any(value => Math.Abs(value - gray) > .10))
            throw new InvalidOperationException($"{label}: composed HDR pixels differ from the reference {white:F3}/{gray:F3}.");
    }

    private static void AssertSdrArtwork(float[] pixels, string label)
    {
        if (pixels.Take(3).Min() < .6f || pixels.Skip(3).Min() < .15f ||
            pixels.Take(3).Max() - pixels.Take(3).Min() > .08 ||
            pixels.Skip(3).Max() - pixels.Skip(3).Min() > .08 ||
            Math.Abs(pixels[3] / pixels[0] - .352f) > .08f)
            throw new InvalidOperationException($"{label}: the composed SDR preview did not contain both artwork tones.");
    }
}
