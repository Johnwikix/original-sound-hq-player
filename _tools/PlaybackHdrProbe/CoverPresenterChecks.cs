using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using WinUIMusicPlayer.Constants;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Helper;

namespace PlaybackHdrProbe;

internal static class CoverPresenterChecks
{
    internal const string JpegFixture = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAAIABgDAREAAhEBAxEB/8QAFgAAAwAAAAAAAAAAAAAAAAAABwkK/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/EABUBAQEAAAAAAAAAAAAAAAAAAAgA/8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEQMRAD8AF6SzAFzIMYOgN3//2Q==";
    private const string PqFixture = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAABEAYAAACprNOOAAAABGNJQ1AJEAABTSMj/gAAABtJREFUeJxjYACD//+nOILg//+bHEAQRjcwAADXFw6msE0u5gAAAABJRU5ErkJggg==";
    private static readonly Rect Slot = new(48, 24, 96, 96);

    internal static async Task RunAsync()
    {
        await CheckSourcesAsync();
        CheckTransitions();
    }

    private static async Task CheckSourcesAsync()
    {
        var dark = await PlaybackSceneCover.ReadDefaultAsync(true, default) ??
            throw new InvalidOperationException("The packaged dark artwork was unreadable.");
        var light = await PlaybackSceneCover.ReadDefaultAsync(false, default) ??
            throw new InvalidOperationException("The packaged light artwork was unreadable.");
        if (dark.Format != DirectXPixelFormat.B8G8R8A8UIntNormalized || light.Format != dark.Format ||
            dark.Width != 744 || dark.Height != 744 || light.Width != 746 || light.Height != 746 ||
            dark.Pixels[0] != 28 || light.Pixels[0] != 238 || dark.Pixels[3] != 255 || light.Pixels[3] != 255)
            throw new InvalidOperationException("The packaged artwork does not match the active theme.");

        string raw = Path.Combine(Path.GetTempPath(), $"playback-art-{Guid.NewGuid():N}");
        string preview = PlaybackCoverImage.GetCachePath(raw);
        try
        {
            if (await PlaybackSceneCover.ReadAsync(raw, true, default) is not null)
                throw new InvalidOperationException("Missing raw artwork was accepted.");
            await File.WriteAllBytesAsync(raw, "<svg/>"u8.ToArray());
            if (await PlaybackSceneCover.ReadAsync(raw, true, default) is not null)
                throw new InvalidOperationException("An unsupported raw format was accepted.");

            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "default_cover_white.png"), preview);
            await File.WriteAllBytesAsync(raw, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
            var fallback = await PlaybackSceneCover.ReadPlaybackAsync(raw, default);
            if (fallback is null || !fallback.Pixels.AsSpan().SequenceEqual(light.Pixels))
                throw new InvalidOperationException("An unreadable PNG blocked the SDR preview fallback.");

            File.Delete(preview);
            fallback = await PlaybackSceneCover.ReadPlaybackAsync(raw, default)
                ?? await PlaybackSceneCover.ReadDefaultAsync(true, default);
            if (fallback is null || !fallback.Pixels.AsSpan().SequenceEqual(dark.Pixels))
                throw new InvalidOperationException("Absent SDR preview blocked the theme fallback.");

            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "default_cover_black.png"), raw, true);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "default_cover_white.png"), preview);
            var cachedAt = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(preview, cachedAt);
            File.SetLastWriteTimeUtc(raw, cachedAt.AddMinutes(-1));
            var warm = await PlaybackSceneCover.ReadPlaybackAsync(raw, default);
            if (warm is null || !warm.Pixels.AsSpan().SequenceEqual(light.Pixels))
                throw new InvalidOperationException("Warm cover decoded the full-sized SDR original instead of using the display cache.");
            File.SetLastWriteTimeUtc(raw, cachedAt.AddMinutes(1));
            var stale = await PlaybackSceneCover.ReadPlaybackAsync(raw, default);
            if (stale is null || !stale.Pixels.AsSpan().SequenceEqual(dark.Pixels))
                throw new InvalidOperationException("An outdated display cache hid a newer original.");

            await File.WriteAllBytesAsync(raw, Convert.FromBase64String(PqFixture));
            File.SetLastWriteTimeUtc(raw, cachedAt.AddMinutes(-1));
            var pq = await PlaybackSceneCover.ReadPlaybackAsync(raw, default);
            if (pq is null || pq.Format != DirectXPixelFormat.R16G16B16A16Float || Math.Abs(pq.PeakNits - 600) > 1)
                throw new InvalidOperationException("A fresh SDR cache replaced the native PQ cover.");

            try
            {
                await PlaybackSceneCover.ReadDefaultAsync(false, new CancellationToken(true));
                throw new InvalidOperationException("A canceled request returned artwork.");
            }
            catch (OperationCanceledException) { }
        }
        finally
        {
            File.Delete(raw);
            File.Delete(preview);
        }

        Console.WriteLine("PASS themed covers, missing or corrupt raw artwork, fresh and stale SDR caches, native PQ priority and cancellation");
    }

    private static void CheckTransitions()
    {
        var red = MakeCover(20, 10, new(3, 0, 0));
        var blue = MakeCover(10, 20, new(0, 0, 5));
        var green = MakeCover(12, 12, new(0, 4, 0));
        using var firstDevice = new CanvasDevice();
        using var target = CreateTarget(firstDevice);
        var clock = new TestTimeProvider();
        using var presenter = new PlaybackSceneCoverPresenter(clock);

        foreach (var mode in Enum.GetValues<ImageSwitchType>())
        {
            presenter.Clear();
            presenter.SetArtwork(red, mode, animate: true);
            var first = Draw(target, presenter);
            CheckPixel(first, 96, 72, new(3, 0, 0), $"first {mode}");
            CheckPixel(first, 96, 40, Vector3.Zero, $"first aspect fit {mode}");
            presenter.SetArtwork(blue, mode, animate: true);
            var start = Draw(target, presenter);
            CheckPixel(start, 96, 72, new(3, 0, 0), $"start {mode}");
            var quarter = Time.AnimationDuration / 4;
            clock.Advance(quarter);
            var early = Draw(target, presenter);
            // XAML samples confirm 4.764% progress at one quarter of the 350 ms transition.
            if (mode == ImageSwitchType.Slide)
            {
                CheckPixel(early, 80, 72, new(2.857f, 0, 0), "quarter Slide outgoing opacity");
                CheckPixel(early, 16, 72, new(0, 0, .238f), "quarter Slide incoming position and opacity");
                CheckPixel(early, 141, 72, Vector3.Zero, "quarter Slide outgoing position");
            }
            else
            {
                CheckPixel(early, 96, 72, new(2.721f, 0, .238f), $"quarter {mode} blend");
            }
            clock.Advance(quarter);
            var middle = Draw(target, presenter);
            int midpointX = mode == ImageSwitchType.Slide ? 48 : 96;
            CheckPixel(middle, midpointX, 72, new(.75f, 0, 2.5f), $"midpoint {mode}");
            CheckPixel(middle, 49, 72, mode switch
            {
                ImageSwitchType.Crossfade => new(1.5f, 0, 0),
                ImageSwitchType.Slide => new(.75f, 0, 2.5f),
                _ => Vector3.Zero
            }, $"geometry {mode}");
            clock.Advance(quarter);
            var late = Draw(target, presenter);
            CheckPixel(late, 96, 72, new(mode == ImageSwitchType.Slide ? 0 : .007f, 0, 4.762f), $"three-quarter {mode}");
            clock.Advance(quarter);
            var completed = Draw(target, presenter);
            CheckPixel(completed, 96, 72, new(0, 0, 5), $"end {mode}");
            CheckPixel(completed, 63, 72, Vector3.Zero, $"end aspect fit {mode}");
            Console.WriteLine($"PASS {mode}: scene FP16 at 144 DPI, blue at 25/50/75% of 350 ms is 0.238/2.500/4.762");
        }

        presenter.SetArtwork(red, ImageSwitchType.Crossfade, animate: true);
        Draw(target, presenter);
        clock.Advance(Time.AnimationDuration / 2);
        Draw(target, presenter);
        presenter.SetArtwork(green, ImageSwitchType.Crossfade, animate: true);
        CheckPixel(Draw(target, presenter), 96, 72, new(3, 0, 0), "rapid switch superseded outgoing cover");
        presenter.SetArtwork(blue, ImageSwitchType.Crossfade, animate: false);
        presenter.ReleaseGpu();
        presenter.ReleaseGpu();
        CheckPixel(Draw(target, presenter), 96, 72, new(0, 0, 5), "released presenter redraws latest artwork without animation");
        presenter.SetArtwork(green, ImageSwitchType.ScaleInOut, animate: true);
        using var secondDevice = new CanvasDevice();
        using var otherTarget = CreateTarget(secondDevice);
        CheckPixel(Draw(otherTarget, presenter), 96, 72, new(0, 0, 5), "device switch retains outgoing cover");
        clock.Advance(Time.AnimationDuration / 2);
        var beforeSwitch = Draw(otherTarget, presenter);
        var afterSwitch = Draw(target, presenter);
        if (!beforeSwitch.AsSpan().SequenceEqual(afterSwitch))
            throw new InvalidOperationException("Moving a running cover transition to another device changed the frame.");
        clock.Advance(Time.AnimationDuration / 2);
        CheckPixel(Draw(target, presenter), 96, 72, new(0, 4, 0), "device switch settles on the new cover");
        CheckSdrCover(firstDevice);
        presenter.SetArtwork(red, ImageSwitchType.Crossfade, animate: false);
        CheckPixel(Draw(otherTarget, presenter), 96, 72, new(3, 0, 0), "disabled animation");

        var shadow = Draw(otherTarget, presenter, shadow: true);
        float near = GetPixel(shadow, 96, 101).X;
        float far = GetPixel(shadow, 96, 110).X;
        if (!(near < far && far <= 1 && GetPixel(shadow, 96, 120).X == 1))
            throw new InvalidOperationException($"The 16 DIP shadow did not fall off across its perimeter: {near}, {far}");
        CheckPixel(shadow, 96, 72, new(3, 0, 0), "shadow preserves cover interior light");
        presenter.Clear();
        using var session = otherTarget.CreateDrawingSession();
        if (presenter.Draw(session, Slot, 12, 16, 200, 600))
            throw new InvalidOperationException("A cleared presenter drew an obsolete cover.");
        Console.WriteLine($"PASS rapid replacement, HDR GPU release/re-entry, new device, immediate mode, clear, and shadow falloff ({near:F4}, {far:F4}, 1.0000)");
    }

    private static void CheckSdrCover(CanvasDevice device)
    {
        using var target = new CanvasRenderTarget(device, 192, 144, 96);
        using var presenter = new PlaybackSceneCoverPresenter();
        presenter.SetArtwork(MakeCover(20, 10, new(.25f)), ImageSwitchType.Crossfade, animate: false);
        using (var session = target.CreateDrawingSession())
        {
            if (!presenter.Draw(session, Slot, 12, 0, 0, 0))
                throw new InvalidOperationException("The native HDR artwork did not draw into SDR.");
        }
        byte[] pixel = target.GetPixelBytes(95, 70, 1, 1);
        // 0.25 linear is approximately 137/255 in sRGB.
        if (pixel.Take(3).Any(channel => Math.Abs(channel - 137) > 2) || pixel[3] != 255)
            throw new InvalidOperationException($"SDR native-cover conversion produced {string.Join(", ", pixel)}.");
        Console.WriteLine("PASS native HDR cover is gamma-encoded for an SDR target.");
    }

    private static CanvasRenderTarget CreateTarget(CanvasDevice device) =>
        new(device, 192, 144, 144, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Ignore);

    private static byte[] Draw(CanvasRenderTarget target, PlaybackSceneCoverPresenter presenter, bool shadow = false)
    {
        using (var session = target.CreateDrawingSession())
        {
            session.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            session.Clear(shadow ? Colors.White : Colors.Black);
            if (!presenter.Draw(session, Slot, 12, shadow ? 16 : 0, 200, 600))
                throw new InvalidOperationException("No cover was drawn.");
        }
        return target.GetPixelBytes();
    }

    private static PlaybackSceneCover MakeCover(int width, int height, Vector3 color)
    {
        byte[] pixels = new byte[width * height * 8];
        var rgba = MemoryMarshal.Cast<byte, Half>(pixels);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (Half)color.X;
            rgba[i + 1] = (Half)color.Y;
            rgba[i + 2] = (Half)color.Z;
            rgba[i + 3] = (Half)1;
        }
        return new(pixels, width, height, DirectXPixelFormat.R16G16B16A16Float, 0);
    }

    private static Vector3 GetPixel(byte[] bytes, int x, int y)
    {
        int offset = (y * 3 / 2 * 288 + x * 3 / 2) * 4;
        var pixels = MemoryMarshal.Cast<byte, Half>(bytes);
        return new((float)pixels[offset], (float)pixels[offset + 1], (float)pixels[offset + 2]);
    }

    private static void CheckPixel(byte[] bytes, int x, int y, Vector3 expected, string operation)
    {
        Vector3 actual = GetPixel(bytes, x, y);
        if (Vector3.Distance(actual, expected) > .015f)
            throw new InvalidOperationException($"{operation}: expected {expected}, got {actual} at ({x}, {y}).");
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
