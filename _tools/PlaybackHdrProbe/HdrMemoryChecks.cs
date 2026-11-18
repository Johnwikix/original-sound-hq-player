using System.Reflection;
using System.Runtime.InteropServices;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Display;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    [DllImport("PlaybackHdrComposition.dll", ExactSpelling = true)]
    private static extern int QueryPlaybackProcessVideoMemory(out ulong local, out ulong nonLocal, out uint adapters);

    private static async Task CheckHdrMemoryAsync(Window window, Grid root)
    {

        DisplayArea? output = null;
        var areas = DisplayArea.FindAll();
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            using var info = DisplayInformation.CreateForDisplayId(area.DisplayId);
            if (info.GetAdvancedColorInfo().CurrentAdvancedColorKind != DisplayAdvancedColorKind.HighDynamicRange) continue;
            output = area;
            break;
        }
        if (output is null)
            throw new InvalidOperationException("The GPU memory check requires an active HDR display.");

        using var displayInfo = DisplayInformation.CreateForDisplayId(output.DisplayId);
        double peak = Math.Min(600, displayInfo.GetAdvancedColorInfo().MaxLuminanceInNits);
        double white = Math.Min(220, peak - 1);
        ulong displayId = output.DisplayId.Value;
        var work = output.WorkArea;
        int width = Math.Min(work.Width, 2560);
        int height = Math.Min(work.Height, 1440);
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        window.Activate();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), 5);
        await Task.Delay(400);
        var empty = await SampleAsync("empty window");

        using var host = new NowPlayingCanvas
        {
            BackgroundShaderIndex = 2,
            IsDark = false,
            IsSnowEnabled = true,
            IsFogEnabled = true,
            LyricsRegion = new Rect(100, 40, 640, 500)
        };
        var hdrDeviceField = typeof(NowPlayingCanvas).GetField("_hdrDevice", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing HDR device ownership field.");
        int hdrFrames = 0;
        bool presented = false, failed = false;
        Exception? failure = null;
        var injected = new InvalidOperationException("Injected HDR frame error");
        var regressions = new List<string>();
        host.HdrPresentationChanged += (active, faulted) => (presented, failed) = (active, faulted);
        host.ExceptionOccurred += (_, error) =>
        {
            if (!ReferenceEquals(error, injected)) failure ??= error;
        };
        host.DrawHdrForeground += (_, _) => hdrFrames++;
        try
        {
            root.Children.Add(host);
            await WaitUntilAsync(() => host.IsLoaded && host.ActualWidth > 0, "memory probe playback host");
            var canvas = FindChild<CanvasAnimatedControl>(host, "PART_Canvas") ?? throw new Exception("Missing SDR canvas");
            var panel = FindChild<CanvasSwapChainPanel>(host, "PART_HdrPanel") ?? throw new Exception("Missing HDR panel");
            int sdrFrames = 0;
            canvas.Draw += (_, _) => Interlocked.Increment(ref sdrFrames);
            TimeProgressBus.SetClock(static () => 900);
            IsPlayingBus.Publish(true);
            PublishLyrics();
            await WaitUntilAsync(() => Volatile.Read(ref sdrFrames) >= 12, "warm SDR renderer");
            var baseline = await SampleAsync("SDR baseline");

            for (int round = 1; round <= 2; round++)
            {
                host.SetHdrPresentation(white, peak, true, displayId);
                await ResumeAsync();
                var hdr = await SampleAsync($"round {round} HDR");
                if (hdr.Total <= baseline.Total + 24)
                    throw new InvalidOperationException("The memory probe did not allocate enough HDR resources.");

                if (round == 1)
                {
                    await CheckPauseAndLuminanceReuseAsync();
                }

                int sdr = Volatile.Read(ref sdrFrames);
                host.SetHdrPresentation(0, 0, true, displayId);
                await WaitUntilAsync(() => !presented && panel.SwapChain is null
                    && Volatile.Read(ref sdrFrames) >= sdr + 8, "SDR fallback after disabling HDR");
                await ExpectReleasedAsync($"round {round} HDR disabled", baseline, hdr);

                host.SetHdrPresentation(white, peak, true, displayId);
                await ResumeAsync();
                await SampleAsync($"round {round} HDR reopened");

                host.PauseRendering();
                await WaitUntilAsync(() => !presented && panel.SwapChain is null, "page closed");
                await ExpectReleasedAsync($"round {round} page closed", baseline, hdr);

                host.ResumeRendering();
                PublishLyrics();
                await ResumeAsync();
                await SampleAsync($"round {round} HDR after re-entering");
                root.Children.Remove(host);
                await WaitUntilAsync(() => !host.IsLoaded && !presented, "playback host unloaded");
                await ExpectReleasedAsync($"round {round} host unloaded", baseline, hdr);

                root.Children.Add(host);
                await WaitUntilAsync(() => host.IsLoaded, "playback host reloaded");
                PublishLyrics();
                await ResumeAsync();
            }

            var beforeFailure = await SampleAsync("before injected HDR failure");
            host.DrawHdrForeground += ThrowFrame;
            await WaitUntilAsync(() => failed && !presented && panel.SwapChain is null, "failed frame fallback");
            host.DrawHdrForeground -= ThrowFrame;
            await ExpectReleasedAsync("failed frame released", baseline, beforeFailure);
            failed = false;
            host.RetryHdrPresentation();
            await ResumeAsync();
            var reopened = await SampleAsync("HDR retry");
            if (reopened.Total <= empty.Total + 24)
                throw new InvalidOperationException("The HDR scene did not restore GPU resources on retry.");

            if (failure is not null) throw failure;
            if (regressions.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, regressions));
            Console.WriteLine("PASS repeated HDR disable, page close, unload, failure and recovery");
        }
        finally
        {
            TimeProgressBus.SetClock(null);
            IsPlayingBus.Publish(false);
            if (root.Children.Contains(host)) root.Children.Remove(host);
        }

        async Task ResumeAsync()
        {
            int before = hdrFrames;
            await WaitUntilAsync(() => failure is not null || failed || presented && hdrFrames >= before + 8,
                "eight real HDR frames");
            if (failure is not null) throw failure;
            if (failed) throw new Exception("HDR presentation could not resume.");
        }

        async Task CheckPauseAndLuminanceReuseAsync()
        {
            var device = hdrDeviceField.GetValue(host);
            if (device is null) throw new Exception("HDR device missing while the scene is rendering.");
            host.SetHdrPresentation(white + .5, peak, true, displayId);
            await ResumeAsync();
            if (!ReferenceEquals(device, hdrDeviceField.GetValue(host)))
                throw new Exception("Luminance adjustment recreated the HDR device.");
            host.SetWindowPaused(true);
            int frames = hdrFrames;
            await Task.Delay(200);
            if (presented || hdrFrames != frames || !ReferenceEquals(device, hdrDeviceField.GetValue(host)))
                throw new Exception("Window pause changed the device or continued drawing HDR frames.");
            host.SetWindowPaused(false);
            await ResumeAsync();
            if (!ReferenceEquals(device, hdrDeviceField.GetValue(host)))
                throw new Exception("Resuming a paused window recreated the HDR device.");
        }

        async Task ExpectReleasedAsync(string label, GpuUsage baseline, GpuUsage hdr)
        {
            int frames = hdrFrames;
            var closed = await SampleAsync(label, settlingMilliseconds: 1600);
            if (hdrFrames != frames)
                throw new Exception($"{label}: HDR kept rendering after stopping.");
            if (hdrDeviceField.GetValue(host) is not null)
                regressions.Add($"{label}: the HDR device is still retained.");
            // WDDM/compositor accounting has a fixed residual that varies by driver;
            // require recovery of the majority of the HDR-specific peak instead of exact zero.
            double allowance = Math.Max(28, (hdr.Total - baseline.Total) * .32);
            if (closed.Total > baseline.Total + allowance)
                regressions.Add($"{label}: retained {closed.Total - baseline.Total:F1} MiB beyond SDR baseline (allowed {allowance:F1}).");
        }

        static void PublishLyrics() => UILyricsBus.Publish([new()
        {
            StartMs = 0, EndMs = 20000,
            Words = { new() { Word = "Memory probe ", StartMs = 0, DurationMs = 2000 },
                      new() { Word = "再生中", StartMs = 2000, DurationMs = 18000 } }
        }]);

        void ThrowFrame(CanvasDrawingSession _, float __) => throw injected;
    }

    private static async Task<GpuUsage> SampleAsync(string label, int settlingMilliseconds = 350)
    {
        await Task.Delay(settlingMilliseconds);
        GpuUsage minimum = new(double.MaxValue, 0);
        uint adapters = 0;
        for (int i = 0; i < 3; i++)
        {
            int hr = QueryPlaybackProcessVideoMemory(out ulong local, out ulong nonLocal, out adapters);
            Marshal.ThrowExceptionForHR(hr);
            var reading = new GpuUsage(local / 1048576d, nonLocal / 1048576d);
            if (reading.Total < minimum.Total) minimum = reading;
            if (i != 2) await Task.Delay(180);
        }
        Console.WriteLine($"{label}: {minimum.Local:F1} MiB local, {minimum.NonLocal:F1} MiB non-local ({adapters} adapter(s))");
        return minimum;
    }

    private readonly record struct GpuUsage(double Local, double NonLocal)
    {
        public double Total => Local + NonLocal;
    }
}
