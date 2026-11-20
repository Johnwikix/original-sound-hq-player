using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer.Background;

internal sealed partial class ProbeApplication
{
    private object CheckBackdropReactivation()
    {
        using var target = CreateFloatTarget(_canvas.Device, 480, 270);
        var frame = new PlaybackCanvasFrame(target.Device, target.Size, target.Dpi, PaperWhite);
        var renderer = typeof(NowPlayingCanvas).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scene = new NowPlayingCanvas { BackgroundShaderIndex = (int)BackgroundShaderMode.RotatingMesh };
        var mesh = (RotatingMeshBackgroundRenderer)renderer.GetValue(scene)!;
        var red = SolidArtwork(230, 22, 25);
        var green = SolidArtwork(12, 218, 36);
        var blue = SolidArtwork(22, 55, 232);
        scene.SetArtwork(red);
        mesh.LoadResources();
        var original = ReadScene(TimeSpan.FromMilliseconds(100));
        if (original.X <= original.Z + .1f || original.X <= original.Y + .1f)
            throw new InvalidOperationException($"The fixture did not render the red original artwork: {original}.");

        scene.BackgroundShaderIndex = (int)BackgroundShaderMode.GradientFlow;
        // Simulate publication of a no-cover track while the cached mesh is not selected.
        scene.SetPalette(null);
        scene.SetArtwork(null);
        scene.BackgroundShaderIndex = (int)BackgroundShaderMode.RotatingMesh;
        if (!ReferenceEquals(mesh, renderer.GetValue(scene)))
            throw new InvalidOperationException("The shader fixture did not reuse its previous mesh renderer.");
        var fallback = ReadScene(TimeSpan.FromMilliseconds(16));
        if (Vector3.Distance(original, fallback) < .1f || fallback.X > fallback.Z + .08f)
            throw new InvalidOperationException($"Reactivating the mesh retained the previous cover: {original} -> {fallback}.");

        scene.SetBackdrop(null, red);
        _ = ReadScene(TimeSpan.FromMilliseconds(16));
        _ = ReadScene(TimeSpan.FromMilliseconds(200));
        scene.SetBackdrop(null, green);
        scene.SetBackdrop(null, blue);
        var intervening = ReadScene(TimeSpan.FromMilliseconds(650));
        var latest = ReadScene(TimeSpan.FromMilliseconds(850));
        if (intervening.X <= intervening.Y + .08f || latest.Z <= latest.Y + .08f || latest.Z <= latest.X + .08f)
            throw new InvalidOperationException($"Superseded green cover entered the queued crossfade: {intervening} -> {latest}.");

        scene.SetBackdrop(null, null);
        _ = ReadScene(TimeSpan.FromMilliseconds(16));
        _ = ReadScene(TimeSpan.FromMilliseconds(850));
        // This deliberately different palette exposes any palette-only frame in the pixel transition.
        var greenPalette = PaletteExtractor.ExtractFromBgra8Pixels(
            SolidArtwork(0, 218, 0).Pixels, ArtworkPixelData.Edge, ArtworkPixelData.Edge, PaletteAlgorithm.OctTree);
        scene.SetBackdrop(greenPalette, blue);
        _ = ReadScene(TimeSpan.FromMilliseconds(16));
        var pairedMix = ReadScene(TimeSpan.FromMilliseconds(400));
        if (pairedMix.Z <= pairedMix.Y + .2f || pairedMix.Z >= latest.Z - .2f)
            throw new InvalidOperationException($"Publishing artwork with its palette did not fade directly to the pixels: {pairedMix}.");

        return new { Original = Rgb(original), Fallback = Rgb(fallback), FirstMix = Rgb(intervening), Latest = Rgb(latest), PairedMix = Rgb(pairedMix) };

        Vector3 ReadScene(TimeSpan advance)
        {
            mesh.Update(advance);
            DrawBackgroundProbe(mesh, frame, target);
            var pixels = target.GetPixelBytes().AsSpan();
            Vector3 total = default;
            int pixelCount = pixels.Length / 8;
            for (int i = 0; i < pixels.Length; i += 8)
            {
                total.X += ReadChannel(pixels[i..]);
                total.Y += ReadChannel(pixels[(i + 2)..]);
                total.Z += ReadChannel(pixels[(i + 4)..]);
            }
            return total / pixelCount;
        }

        static float ReadChannel(ReadOnlySpan<byte> pixels) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels));
        static float[] Rgb(Vector3 x) => [x.X, x.Y, x.Z];
    }

    private static ArtworkPixelData SolidArtwork(byte red, byte green, byte blue)
    {
        var pixels = new byte[ArtworkPixelData.Edge * ArtworkPixelData.Edge * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = 255;
        }
        return new ArtworkPixelData(pixels);
    }
}
