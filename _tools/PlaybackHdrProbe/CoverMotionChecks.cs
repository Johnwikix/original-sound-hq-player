using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Constants;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Helper;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern int SampleMotionFrames(nint hwnd, [In] PointInt32[] points, int count,
        [Out] float[] rgb, [Out] long[] times, int maximumFrames, out int frames);

    private static async Task CheckCoverMotionAsync(Window window)
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            throw new InvalidOperationException("Enable Windows animation effects to measure XAML cover motion.");

        var root = new Canvas { Background = new SolidColorBrush(Colors.Black) };
        window.Content = root;
        window.AppWindow.MoveAndResize(new RectInt32(100, 100, 1000, 760));
        App.MainWindow = window;
        window.Activate();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(hwnd, 5);
        await WaitUntilAsync(() => root.IsLoaded && root.ActualWidth >= 370 && root.ActualHeight >= 270, "cover motion probe layout");
        double dpi = root.XamlRoot.RasterizationScale;

        using var red = new TempPng("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        using var blue = new TempPng("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYPgPAAEDAQAIicLsAAAAAElFTkSuQmCC");
        var samplePoints = new List<PointInt32>();
        AddPoint(15, 20); // start marker
        AddPoint(45, 20); // outgoing alpha in isolation
        AddPoint(75, 20); // opaque red reference
        AddPoint(105, 20); // red at half opacity
        AddPoint(135, 20); // incoming alpha in isolation
        for (int x = 20; x <= 240; x++) AddPoint(x, 60);
        for (int x = 20; x <= 240; x++) AddPoint(x, 110);
        for (int x = 20; x <= 365; x++) AddPoint(x, 204);

        foreach (var mode in Enum.GetValues<ImageSwitchType>())
        {
            var marker = AddSwatch(Colors.Lime, 5, 10, 20);
            marker.Opacity = 0;
            var fadeOut = AddSwatch(Colors.Red, 35, 10, 20);
            var fadeIn = AddSwatch(Colors.Red, 125, 10, 20);
            fadeIn.Opacity = 0;
            AddSwatch(Colors.Red, 65, 10, 20);
            var half = AddSwatch(Colors.Red, 95, 10, 20);
            half.Opacity = .5;
            var slide = AddSwatch(Colors.Red, 30, 50, 20);
            var scale = AddSwatch(Colors.Red, 30, 100, 200);
            var cover = new ImageSwitcher { Width = 120, Height = 120, SwitchType = mode, ImageHash = red.Path };
            Canvas.SetLeft(cover, 175);
            Canvas.SetTop(cover, 144);
            root.Children.Add(cover);
            await WaitUntilAsync(() => cover.IsCoverSettled(red.Path), $"{mode} starting image");
            await Task.Delay(100);

            var image = (ShadowImage)cover.FindName("AlbumArtImage");
            long token = image.RegisterPropertyChangedCallback(ShadowImage.SourceProperty, (_, _) =>
            {
                if (image.Source is null) return;
                marker.Opacity = 1;
                fadeOut.OpacityTransition = TransitionCache.Default;
                fadeIn.OpacityTransition = TransitionCache.Default;
                slide.TranslationTransition = TransitionCache.DefaultVector3;
                scale.ScaleTransition = TransitionCache.DefaultVector3;
                fadeOut.Opacity = 0;
                fadeIn.Opacity = 1;
                slide.Translation = new Vector3(200, 0, 0);
                scale.Scale = new Vector3(.5f, 1, 1);
            });
            const int frameLimit = 144;
            PointInt32[] points = samplePoints.ToArray();
            var pixels = new float[points.Length * 3 * frameLimit];
            var times = new long[frameLimit];
            var capture = Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(SampleMotionFrames(hwnd, points, points.Length, pixels, times, frameLimit, out int count));
                return count;
            });
            try
            {
                await Task.Delay(150);
                cover.ImageHash = blue.Path;
                var timeline = ReadMotionFrames(mode, pixels, times, points.Length, await capture);
                CheckXamlMotion(mode, timeline, dpi);
            }
            finally
            {
                image.UnregisterPropertyChangedCallback(ShadowImage.SourceProperty, token);
                root.Children.Clear();
            }
        }

        await CoverPresenterChecks.RunAsync();

        void AddPoint(int x, int y) => samplePoints.Add(new PointInt32((int)Math.Round(x * dpi), (int)Math.Round(y * dpi)));

        Border AddSwatch(Windows.UI.Color color, int x, int y, int width)
        {
            var border = new Border { Width = width, Height = 20, Background = new SolidColorBrush(color) };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            root.Children.Add(border);
            return border;
        }
    }

    private readonly record struct MotionFrame(double Time, float AlphaOut, float AlphaIn, float Slide, float Scale,
        int RedLeft, int RedRight, int BlueLeft, int BlueRight);

    private static List<MotionFrame> ReadMotionFrames(
        ImageSwitchType mode, float[] pixels, long[] times, int points, int count)
    {
        var samples = new List<MotionFrame>();
        long start = 0;
        for (int frame = 0; frame < count; frame++)
        {
            int offset = frame * points * 3;
            float Pixel(int x, int channel = 0) => pixels[offset + x * 3 + channel];
            if (Pixel(0, 1) <= .5f) continue;
            if (start == 0) start = times[frame];
            float white = Pixel(2);
            if (white <= .5f) throw new InvalidOperationException("No opaque reference swatch.");
            bool srgbBlend = Pixel(3) / white < .35f;
            float Alpha(float pixel)
            {
                float channel = pixel / white;
                if (!srgbBlend) return channel;
                return channel <= .0031308f ? channel * 12.92f : 1.055f * MathF.Pow(channel, 1 / 2.4f) - .055f;
            }
            int slideLeft = Enumerable.Range(0, 221).First(i => Pixel(i + 5) > white * .5f) + 20;
            int scaleRight = Enumerable.Range(0, 221).Last(i => Pixel(i + 226) > white * .5f) + 20;
            (int Left, int Right) CoverRange(int channel)
            {
                int first = -1, last = -1;
                for (int x = 20; x <= 365; x++)
                {
                    if (Pixel(447 + x - 20, channel) < white * .025f) continue;
                    if (first < 0) first = x;
                    last = x;
                }
                return (first, last);
            }

            var red = CoverRange(0);
            var blue = CoverRange(2);
            samples.Add(new((times[frame] - start) / (double)TimeSpan.TicksPerMillisecond,
                Alpha(Pixel(1)), Alpha(Pixel(4)), (slideLeft - 30) / 200f, (229 - scaleRight) / 100f,
                red.Left, red.Right, blue.Left, blue.Right));
        }
        if (samples.Count < 8) throw new InvalidOperationException($"Captured too few frames of {mode} motion ({samples.Count}).");
        return samples;
    }

    private static void CheckXamlMotion(ImageSwitchType mode, List<MotionFrame> frames, double dpi)
    {
        double duration = Time.AnimationDuration.TotalMilliseconds;
        // The marker and the animated visuals can reach the compositor one display frame apart.
        double framePeriod = frames.Zip(frames.Skip(1), (a, b) => b.Time - a.Time).Order().ElementAt((frames.Count - 1) / 2);
        if (frames[0].Slide > .02f || frames[^1].Slide < .995f)
            throw new InvalidOperationException($"{mode}: did not observe both ends of the XAML cover transition.");
        double settled = TimeAt(.999f);
        if (Math.Abs(settled - duration) > framePeriod + 3)
            throw new InvalidOperationException($"{mode}: XAML cover reached its final position at {settled:F1} ms, expected {duration:F0} ms.");

        // The 25% and 75% time landmarks of the Fluent standard cubic are 4.764% and 95.236% progress.
        // Align to the measured midpoint to remove the compositor's start phase from the curve comparison.
        double midpoint = TimeAt(.5f);
        if (Math.Abs((midpoint - TimeAt(.047644f)) - duration / 4) > 12 ||
            Math.Abs((TimeAt(.952356f) - midpoint) - duration / 4) > 12)
            throw new InvalidOperationException($"{mode}: XAML's transition did not match the measured Fluent standard easing.");
        if (frames.Any(frame => Math.Abs(1 - frame.AlphaOut - frame.Slide) > .025f ||
            Math.Abs(frame.AlphaIn - frame.Slide) > .025f || Math.Abs(frame.Scale - frame.Slide) > .025f))
            throw new InvalidOperationException($"{mode}: the XAML scalar and vector transitions progressed differently.");

        var middle = frames.Where(frame => frame.Slide is >= .2f and <= .62f).MinBy(frame => Math.Abs(frame.Slide - .5f));
        float progress = middle.Slide;
        if (progress < .2f) throw new InvalidOperationException($"{mode}: no composited frame sampled near the middle.");
        var expected = mode switch
        {
            ImageSwitchType.Slide => (175 - progress * 120, 295 - (1 - progress) * 120),
            ImageSwitchType.ScaleInOut => (175 + progress * 12, 295 - (1 - progress) * 9),
            _ => (175f, 295f)
        };
        if (Math.Abs(middle.RedLeft - expected.Item1) > 2.5f || Math.Abs(middle.BlueRight - expected.Item2) > 2.5f ||
            frames[0].RedLeft != 175 || frames[^1].BlueRight < 293)
            throw new InvalidOperationException($"{mode}: XAML cover did not match the measured transition geometry.");
        Console.WriteLine($"PASS XAML {mode} at {dpi * 96:F0} DPI: {frames.Count} frames, 5/50/95% at {TimeAt(.05f):F1}/{midpoint:F1}/{TimeAt(.95f):F1} ms, settled at {settled:F1} ms; middle alpha {middle.AlphaOut:F3}/{middle.AlphaIn:F3}, edges {middle.RedLeft} and {middle.BlueRight}");

        double TimeAt(float progress)
        {
            for (int i = 1; i < frames.Count; i++)
            {
                MotionFrame next = frames[i];
                if (next.Slide < progress) continue;
                MotionFrame prior = frames[i - 1];
                float fraction = (progress - prior.Slide) / (next.Slide - prior.Slide);
                return prior.Time + (next.Time - prior.Time) * fraction;
            }
            throw new InvalidOperationException($"{mode}: no frame reached {progress:P1} progress.");
        }
    }

    private sealed class TempPng : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cover-motion-{Guid.NewGuid():N}.png");
        internal TempPng(string png) => File.WriteAllBytes(Path, Convert.FromBase64String(png));
        public void Dispose() => File.Delete(Path);
    }
}
