using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    private static async Task CheckLyricsAcrylicAsync(Window window)
    {
        var vm = new AppViewModel();
        WinUIMusicPlayer.App.MainWindow = window;
        using var page = new PlayingDetailPage(vm);
        window.Content = page;
        window.SystemBackdrop = new CustomAcrylicSystemBackdrop(window) { TintOpacity = 0, LuminosityOpacity = .4 };
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        window.Activate();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 5);
        var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Move(new PointInt32(workArea.X + workArea.Width / 3, workArea.Y + workArea.Height / 6));
        double scale = GetDpiForWindow(hwnd) / 96d;
        SizeInt32 Pixels(double width, double height) => new((int)(width * scale), (int)(height * scale));

        try
        {
            window.AppWindow.ResizeClient(Pixels(780, 535));
            await WaitUntilAsync(() => page.StandaloneLyrics is { Visibility: Visibility.Visible, ActualWidth: > 100 },
                "Untransformed right-pane lyric host");
            var lyrics = page.StandaloneLyrics;
            await WaitUntilAsync(() => Math.Abs(lyrics.ActualWidth - page.LyricsPane.Width) < 2, "Prior right-pane width");
            double originalWidth = lyrics.ActualWidth;
            PublishLyrics(fan: false);
            await Task.Delay(500);
            CheckLyricInk(originalWidth, page.ActualWidth, page.ActualHeight);
            vm.IsFanLyricsEnabled = true;
            await WaitUntilAsync(() => lyrics.UseTransparentPresentation && lyrics.ActualWidth > originalWidth * 1.4,
                "Widened landscape lyric surface");
            PublishLyrics(fan: true);
            nint landscapeBackdrop = ShowBackdrop(hwnd);
            try { await CheckSurfaceAndDwm("landscape with fan", portrait: false, landscapeBackdrop); }
            finally { if (landscapeBackdrop != 0) DestroyWindow(landscapeBackdrop); }
            vm.IsFanLyricsEnabled = false;
            await WaitUntilAsync(() => lyrics.UseTransparentPresentation && Math.Abs(lyrics.ActualWidth - originalWidth) < 2,
                "Untransformed landscape restored");
            PublishLyrics(fan: false);
            await Task.Delay(300);
            CheckLyricInk(originalWidth, page.ActualWidth, page.ActualHeight);
            vm.IsFluidBackgroundEnabled = true;
            await WaitUntilAsync(() => !lyrics.IsActive && FindLyricImage(lyrics)?.Source is null && page.BackgroundCanvas is not null,
                "Colored scene releases the acrylic lyric image");
            vm.IsFluidBackgroundEnabled = false;
            await WaitUntilAsync(() => lyrics.IsActive && FindLyricImage(lyrics)?.Source is CanvasImageSource,
                "Acrylic lyric image resumes after leaving the colored scene");

            window.AppWindow.ResizeClient(Pixels(445, 665));
            await WaitUntilAsync(() => vm.IsPortraitLayout && lyrics.UseTransparentPresentation, "Portrait acrylic presentation");
            PublishLyrics(fan: false);
            nint portraitBackdrop = ShowBackdrop(hwnd);
            try
            {
                await CheckSurfaceAndDwm("portrait zero margin", portrait: true, portraitBackdrop);
                ChangeBackdrop(portraitBackdrop);
                vm.LyricsMargin = new Thickness(.5, 0, .5, 0);
                await CheckSurfaceAndDwm("portrait 0.5 DIP margin", portrait: true, portraitBackdrop);
            }
            finally { if (portraitBackdrop != 0) DestroyWindow(portraitBackdrop); }
            vm.LyricsMargin = new Thickness(1200, 0, 1200, 0);
            await WaitUntilAsync(() => lyrics.ActualWidth is > 0 and < 4,
                "Oversized margins keep a valid local surface");
            vm.LyricsMargin = new Thickness();
            await WaitUntilAsync(() => FindLyricImage(lyrics)?.Source is CanvasImageSource && lyrics.ActualWidth > 400,
                "Normal portrait margins recover");
        }
        finally
        {
            UILyricsBus.Publish([]);
            window.SystemBackdrop = null;
            window.Content = new Grid();
        }

        void CheckLyricInk(double paneWidth, double pageWidth, double pageHeight)
        {
            var points = (from y in Enumerable.Range(0, 21)
                          from x in Enumerable.Range(0, 31)
                          select new PointInt32(
                              (int)((pageWidth - paneWidth + 12 + (paneWidth - 30) * x / 30) * scale),
                              (int)(pageHeight * scale * (.27 + .46 * y / 20)))).ToArray();
            float[] rgb = new float[points.Length * 3];
            if (SampleFrame(hwnd, true, points, points.Length, rgb) < 0)
                throw new InvalidOperationException("Could not capture the actual lyric window.");
            int bright = Enumerable.Range(0, points.Length)
                .Count(i => rgb[3 * i] > .6f && rgb[3 * i + 1] > .6f && rgb[3 * i + 2] > .6f);
            if (bright < 5) throw new InvalidOperationException("Expected visible lyric ink before or after changing the presentation.");
            Console.WriteLine($"Active lyric window captured {bright} bright text samples.");
        }

        async Task CheckSurfaceAndDwm(string label, bool portrait, nint backing)
        {
            var lyrics = page.StandaloneLyrics;
            await WaitUntilAsync(() => FindLyricImage(lyrics)?.Source is CanvasImageSource, $"{label} Win2D image source");
            var image = FindLyricImage(lyrics)!;
            var bounds = image.TransformToVisual(page).TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
            if (bounds.Left < (portrait ? 7 : page.ActualWidth * .24)
                || bounds.Right > page.ActualWidth - (portrait ? 7 : -1)
                || (!portrait && bounds.Left >= page.LyricsPane.Left - 25))
                throw new InvalidOperationException($"{label}: render surface {bounds} did not fit the local viewport");
            Console.WriteLine($"{label}: {GetDpiForWindow(hwnd)} DPI, surface={bounds}, page={page.ActualWidth:F1}x{page.ActualHeight:F1}");

            if (backing == 0) throw new InvalidOperationException("Could not create desktop color reference.");
            await Task.Delay(400);
            PointInt32[] samples =
            [
                new((int)(page.ActualWidth * scale * (portrait ? .01 : .15)), (int)(page.ActualHeight * scale * .15)),
                new((int)(page.ActualWidth * scale * .82), (int)(page.ActualHeight * scale * .15))
            ];
            PointInt32[] reference = [new(7, 100)];
            bool measured = TrySample(backing, reference, $"{label} red reference", out var referenceRed);
            float[]? red = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TrySample(hwnd, samples, $"{label} red", out var captured) &&
                    Enumerable.Range(0, samples.Length).All(i => captured[3 * i] > captured[3 * i + 2] + .015f))
                {
                    red = captured;
                    break;
                }
                await Task.Delay(350);
            }
            if (red is null) throw new InvalidOperationException($"{label}: acrylic did not transmit the red reference.");
            ChangeBackdrop(backing);
            await Task.Delay(550);
            measured &= TrySample(backing, reference, $"{label} blue reference", out var referenceBlue);
            if (!measured || referenceRed[0] <= referenceBlue[0] + .5f || referenceBlue[2] <= referenceRed[2] + .5f)
                throw new InvalidOperationException($"{label}: desktop color capture did not follow the red/blue reference.");

            // The backdrop effect and Windows Graphics Capture update independently.
            // Wait for the captured acrylic frame to catch up to the already-blue source.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TrySample(hwnd, samples, $"{label} blue", out var blue) &&
                    Enumerable.Range(0, samples.Length).All(i => red[3 * i] > blue[3 * i] + .015f
                        && blue[3 * i + 2] > red[3 * i + 2] + .015f))
                    return;
                await Task.Delay(350);
            }
            throw new InvalidOperationException($"{label}: acrylic did not transmit the change behind the live lyric window.");
        }
    }

    private static void PublishLyrics(bool fan)
    {
        LyricsSettingsBus.Publish(new LyricsSettingsBus.Settings("Segoe UI", CanvasHorizontalAlignment.Left,
            true, 1, 0, 0, 0, 100, 1200, false, false, .8, .8, 0, EasingType.Sine,
            EaseMode.Out, .45, 60, true, Colors.White, isFanLyricsEnabled: fan, fanLyricsAngle: 65));
        LyricsFontSizeBus.Publish(42);
        UILyricsBus.Publish(Enumerable.Range(0, 9).Select(i =>
        {
            var line = new LyricLine { StartMs = i * 2500, EndMs = (i + 1) * 2500 };
            line.Words.Add(new LyricWord { Word = "Lyrics across the center", StartMs = i * 2500, DurationMs = 2500 });
            return line;
        }).ToList());
        IsPlayingBus.Publish(false);
        TimeProgressBus.Publish(9150);
    }

    private static Image? FindLyricImage(DependencyObject node)
    {
        if (node is Image { Name: "PART_TransparentCanvas" } image) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindLyricImage(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
}
