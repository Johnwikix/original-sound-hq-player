using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    private static async Task CheckDelayedShaderCoverAsync(Window window, string first, string next,
        bool hdr, bool hdrOutput, ulong displayId, double white, double peak)
    {
        string label = hdr ? (hdrOutput ? "HDR scene on HDR output" : "FP16 scene on SDR output")
            : hdrOutput ? "SDR scene on HDR output" : "SDR scene on SDR output";
        var app = new AppViewModel
        {
            DisplayedPlaybackMusic = new object(),
            LyricPageBackgroundHash = first,
            IsFluidBackgroundEnabled = true,
            PlaybackHdrEnabled = hdr,
            PlaybackHdrDisplayId = displayId,
            PlaybackHdrWhiteNits = white,
            PlaybackHdrEffectivePeakNits = peak
        };
        using var page = new PlayingDetailPage(app);
        var marker = new Border { Background = new SolidColorBrush(Colors.Blue), Width = 20, Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var root = new Grid();
        root.Children.Add(page);
        root.Children.Add(marker);
        window.Content = root;
        try
        {
            await WaitUntilAsync(() => page.Cover.Opacity == 0 && app.PlaybackHdrActive == hdr && page.CoverSlot.Height > 100,
                $"{label}: foreground shader cover is visible");
            var art = Fit(page.CoverSlot);
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            double dpi = root.XamlRoot.RasterizationScale;
            PointInt32 Point(double x, double y) => new((int)Math.Round(x * dpi), (int)Math.Round(y * dpi));
            PointInt32[] points = [Point(10, 10), Point(art.X + art.Width * .22, art.Y + art.Height * .5),
                Point(art.X + art.Width * .78, art.Y + art.Height * .5)];
            int stride = points.Length * 3;
            var baseline = new float[stride];
            Marshal.ThrowExceptionForHR(SampleFrame(hwnd, true, points, points.Length, baseline));
            if (baseline[3] < .65f || baseline[6] < .15f || baseline[6] > baseline[3] * .7f)
                throw new InvalidOperationException($"{label}: foreground cover reference lacks its expected two tones.");

            int previewReveals = 0;
            long opacityObserver = page.Cover.RegisterPropertyChangedCallback(UIElement.OpacityProperty, (_, _) =>
            {
                if (page.Cover.Opacity != 0) previewReveals++;
            });
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
            using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
            const int frameLimit = 480;
            var rgb = new float[frameLimit * stride];
            var times = new long[frameLimit];
            var capture = Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(SampleNavigationFrames(hwnd, points, points.Length, rgb, times, frameLimit,
                    out int frames, ready.SafeWaitHandle.DangerousGetHandle(), completed.SafeWaitHandle.DangerousGetHandle()));
                return frames;
            });
            int count;
            try
            {
                await WaitUntilAsync(() => ready.WaitOne(0) || capture.IsCompleted, $"{label}: capture is ready");
                if (capture.IsCompleted) throw new InvalidOperationException($"{label}: capture stopped early after {await capture} frames.");

                // A selection changes first. The cover pipeline publishes the new hash only when its artwork is ready.
                app.DisplayedPlaybackMusic = new object();
                marker.Background = new SolidColorBrush(Colors.Lime);
                await Task.Delay(350);
                marker.Background = new SolidColorBrush(Colors.Red);
                app.LyricPageBackgroundHash = next;
                await Task.Delay(750);
            }
            finally
            {
                completed.Set();
                page.Cover.UnregisterPropertyChangedCallback(UIElement.OpacityProperty, opacityObserver);
                count = await capture;
            }

            int heldFrames = 0, mixedFrames = 0, settledFrames = 0;
            long holdFirst = 0, holdLast = 0;
            for (int frame = 0; frame < count; frame++)
            {
                var row = rgb.AsSpan(frame * stride, stride);
                // The marker changes in the same UI turn as each test phase.
                if (row[1] > row[0] + .25f && row[1] > row[2] + .25f)
                {
                    for (int channel = 3; channel < stride; channel++)
                        if (Math.Abs(row[channel] - baseline[channel]) > Math.Max(.08f, baseline[channel] * .045f))
                            throw new InvalidOperationException($"{label}: frame {frame} changed the old cover while artwork was pending.");
                    if (heldFrames++ == 0) holdFirst = times[frame];
                    holdLast = times[frame];
                }
                else if (row[0] > row[1] + .25f && row[0] > row[2] + .25f)
                {
                    float red = row[3], leftGreen = row[4], rightGreen = row[7], blue = row[8];
                    if (red < baseline[3] * .4f || blue < baseline[6] * .35f)
                        throw new InvalidOperationException($"{label}: cover disappeared during frame {frame} of the transition.");
                    if (red - leftGreen > baseline[3] * .08f && leftGreen > baseline[3] * .14f && blue > rightGreen + baseline[6] * .08f)
                        mixedFrames++;
                    if (red > leftGreen * 4 && blue > rightGreen * 4 && leftGreen < baseline[3] * .10f)
                        settledFrames++;
                }
            }
            double heldMs = (holdLast - holdFirst) / (double)TimeSpan.TicksPerMillisecond;
            if (heldFrames < 4 || heldMs < 200 || mixedFrames < 2 || settledFrames < 2 || previewReveals != 0 || page.Cover.Opacity != 0)
                throw new InvalidOperationException($"{label}: expected retained cover, actual motion and continuous scene ownership. " +
                    $"Captured {count}, held {heldFrames}/{heldMs:F0}ms, mixed {mixedFrames}, settled {settledFrames}, XAML reveals {previewReveals}.");
            Console.WriteLine($"PASS {label}: {heldFrames} unchanged foreground frames over {heldMs:F0} ms while cover was pending, " +
                $"{mixedFrames} animated frames, {settledFrames} settled frames, {previewReveals} XAML reveals.");
        }
        finally { window.Content = new Grid(); }
    }
}
