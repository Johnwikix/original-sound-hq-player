using System;
using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace AnimatedWin2dControls.Renderer.Background;

/// <summary>播放页和导出海报共用着色器实现。海报持有渲染器只到静态帧回读结束。</summary>
public static class BackgroundShaderRenderer
{
    internal static BaseBackgroundRenderer Create(BackgroundShaderMode mode) => mode switch
    {
        BackgroundShaderMode.PS3XMB => new PS3XMBBackgroundRenderer(),
        BackgroundShaderMode.RotatingMesh => new RotatingMeshBackgroundRenderer(),
        BackgroundShaderMode.LiquidFlow => new LiquidFlowBackgroundRenderer(),
        BackgroundShaderMode.GradientFlow => new GradientFlowBackgroundRenderer(),
        BackgroundShaderMode.WavyBackground => new WavyBackgroundRenderer(),
        BackgroundShaderMode.ChromaticResonance => new ChromaticResonanceBackgroundRenderer(),
        _ => new FluidBackgroundRenderer(),
    };

    /// <summary>从已解码的海报封面生成一张带文字暗罩的 SDR 静态背景。</summary>
    public static async Task<SoftwareBitmap> RenderCoverStillAsync(SoftwareBitmap cover,
        BackgroundShaderMode mode, PaletteAlgorithm algorithm, CancellationToken token)
    {
        const int width = 432;
        const int height = 540;
        token.ThrowIfCancellationRequested();
        var (palette, artwork) = PrepareCover(cover, mode, algorithm);
        token.ThrowIfCancellationRequested();

        using var renderer = Create(mode);
        renderer.SetPalette(palette);
        renderer.LoadResources();
        if (artwork is not null) renderer.SetArtwork(artwork);
        renderer.Update(TimeSpan.FromSeconds(1));
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, width, height, 96);
        var size = new Size(width, height);
        using (var session = CanvasDrawingSessionScope.Create(target))
        {
            var draw = session.Context;
            draw.Clear(Color.FromArgb(255, 18, 25, 28));
            renderer.Draw(new PlaybackCanvasFrame(device, size, 96, default), draw);
            draw.FillRectangle(new Rect(default(Point), size), Color.FromArgb(153, 0, 0, 0));
        }
        token.ThrowIfCancellationRequested();
        // 直接从 D3D 表面回读到原生位图，后台渲染无需传递大托管数组。
        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(target, BitmapAlphaMode.Premultiplied)
            .AsTask().ConfigureAwait(false);
    }

    private static (PaletteResult Palette, ArtworkPixelData? Artwork) PrepareCover(SoftwareBitmap bitmap,
        BackgroundShaderMode mode, PaletteAlgorithm algorithm)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || bitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied
            || width is <= 0 or > 256 || height is <= 0 or > 256)
            throw new ArgumentException("A premultiplied BGRA8 cover up to 256 pixels per side is required.", nameof(bitmap));

        int length = width * height * 4;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            bitmap.CopyToBuffer(buffer.AsBuffer(0, length));
            ReadOnlySpan<byte> pixels = buffer.AsSpan(0, length);
            var palette = PaletteExtractor.ExtractFromBgra8Pixels(pixels, width, height, algorithm);
            var artwork = mode == BackgroundShaderMode.RotatingMesh
                ? ArtworkPixelDecoder.ConvertToSquareRgba8(pixels, width, height, ArtworkPixelData.Edge)
                : null;
            return (palette, artwork);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
