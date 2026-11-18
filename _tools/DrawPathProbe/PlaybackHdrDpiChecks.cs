using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Messages;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Graphics.DirectX;

internal sealed partial class ProbeApplication
{
    private object MeasureHdrDpi()
    {
        const float dpi = 144f;
        var device = _canvas.Device;
        using var target = new CanvasRenderTarget(device, 480, 270, dpi,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        using var cache = new CanvasRenderTarget(device, 480, 270, dpi,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        if (target.Dpi != dpi || target.SizeInPixels.Width != 720 || target.SizeInPixels.Height != 405)
            throw new InvalidOperationException("HDR test must draw to a 144-DPI target with 1.5x physical pixels.");

        var frame = new PlaybackCanvasFrame(device, target.Size, dpi, Hdr);
        var backgrounds = new List<object>();
        foreach (var renderer in CreateBackgrounds())
        {
            using (renderer)
            {
                renderer.IsDark = false;
                if (renderer is RotatingMeshBackgroundRenderer)
                {
                    byte[] whiteArtwork = new byte[ArtworkPixelData.Edge * ArtworkPixelData.Edge * 4];
                    Array.Fill(whiteArtwork, (byte)255);
                    renderer.SetArtwork(new ArtworkPixelData(whiteArtwork));
                }
                renderer.LoadResources();
                renderer.Update(TimeSpan.FromSeconds(10));
                DrawBackground(renderer, target, false);
                float defaultPeak = ReadMax(target).Rgb;
                DrawBackground(renderer, target, true);
                byte[] directPixels = target.GetPixelBytes();
                DrawBackground(renderer, cache, false);
                for (int i = 0; i < 2; i++)
                {
                    using (var drawing = target.CreateDrawingSession())
                    {
                        drawing.Clear(Colors.Transparent);
                        drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                        drawing.DrawImage(cache);
                    }
                    if (!directPixels.AsSpan().SequenceEqual(target.GetPixelBytes()))
                        throw new InvalidOperationException($"{renderer.GetType().Name} changed pixels during 144-DPI frame reuse.");
                }
                var direct = ReadMax(directPixels);
                if (direct.Rgb <= 1.05f || direct.Alpha != 1)
                    throw new InvalidOperationException($"{renderer.GetType().Name} produced invalid 144-DPI background pixels.");
                backgrounds.Add(new { Shader = renderer.GetType().Name, DefaultPrecision = defaultPeak, FloatPrecision = direct.Rgb, Cached = direct.Rgb });
            }
        }

        var image = MeasureImage();
        var lyrics = MeasureScaledLyrics();
        return new { CanvasDpi = _canvas.Dpi, Backgrounds = backgrounds, Image = image, Lyrics = lyrics };

        void DrawBackground(BaseBackgroundRenderer renderer, CanvasRenderTarget destination, bool floatEffects)
        {
            using var drawing = destination.CreateDrawingSession();
            drawing.Clear(Colors.Transparent);
            drawing.EffectBufferPrecision = floatEffects ? CanvasBufferPrecision.Precision16Float : null;
            using var scope = CanvasDrawingSessionScope.Borrow(drawing);
            renderer.Draw(frame, scope.Context);
        }

        object MeasureImage()
        {
            const int size = 24;
            var type = typeof(BaseBackgroundRenderer).Assembly.GetType(
                "AnimatedWin2dControls.Renderer.Background.ImageBackgroundRenderer", throwOnError: true)!;
            using var renderer = (BaseBackgroundRenderer)Activator.CreateInstance(type, string.Empty, 4d)!;
            renderer.IsDark = false;
            renderer.LoadResources();

            // Supply a deterministic image at the decoder-to-renderer boundary.
            // The uploaded bitmap, blur, source crop, upscaling and theme veil are all production draws.
            var pendingType = type.GetNestedType("PendingImage", BindingFlags.NonPublic)!;
            var pendingField = type.GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            byte[] white = new byte[size * size * 4];
            Array.Fill(white, (byte)255);
            pendingField.SetValue(renderer, Activator.CreateInstance(pendingType, white, size, size, 0));

            DrawBackground(renderer, target, false);
            float defaultPeak = Center();
            DrawBackground(renderer, target, true);
            float floatPeak = Center();
            if (Math.Abs(floatPeak - 2.5f) > 0.015f || Math.Abs(defaultPeak - floatPeak) > 0.015f)
                throw new InvalidOperationException($"Scaled blurred HDR image lost reference white at 144 DPI ({defaultPeak}, {floatPeak}).");

            // A second decode can finish after the first pending result has already restored a device.
            byte[] black = new byte[white.Length];
            for (int i = 3; i < black.Length; i += 4) black[i] = 255;
            pendingField.SetValue(renderer, Activator.CreateInstance(pendingType, black, size, size, 0));
            DrawBackground(renderer, target, true);
            float replacedPeak = Center();
            if (Math.Abs(replacedPeak - 2f) > .015f)
                throw new InvalidOperationException($"The HDR effect retained the previous upload after a same-source refresh ({replacedPeak}).");

            return new { DefaultPrecision = defaultPeak, FloatPrecision = floatPeak, ReplacedPeak = replacedPeak };

            float Center()
            {
                int x = (int)target.SizeInPixels.Width / 2, y = (int)target.SizeInPixels.Height / 2;
                return ReadHalf(target.GetPixelBytes(x, y, 1, 1), 0);
            }
        }

        object MeasureScaledLyrics()
        {
            using var lyricsTarget = new CanvasRenderTarget(device, 1100, 700, dpi,
                DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
            const long position = 740;
            TimeProgressBus.SetClock(static () => position);
            try
            {
                IsPlayingBus.Publish(true);
                LyricsFontSizeBus.Publish(54);
                LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, true,
                    1, 0, 0, 0, 30, 100, false, false, 0.5, 0.7, 0,
                    EasingType.Sine, EaseMode.Out, 0.35, 60, false, Colors.White));
                UILyricsBus.Publish([new LyricLine
                {
                    EndMs = 2000,
                    Words =
                    [
                        new() { Word = "MMMM", StartMs = 0, DurationMs = 1000 },
                        new() { Word = "MMMM", StartMs = 1000, DurationMs = 1000 }
                    ]
                }]);
                for (int i = 0; i < 45; i++) _lyrics.OnUpdate(lyricsTarget, Delta);

                var field = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var lines = (List<RenderLyricsLine>)field.GetValue(_lyrics)!;
                foreach (var ch in lines[0].PrimaryRenderChars)
                    ch.ScaleTransition.JumpTo(1.25);

                float defaultPeak = DrawScaled(false);
                float floatPeak = DrawScaled(true);
                if (Math.Abs(floatPeak - Hdr.White) > 0.04f || Math.Abs(defaultPeak - floatPeak) > 0.025f)
                    throw new InvalidOperationException($"144-DPI lyrics lost highlight during per-glyph scaling ({defaultPeak}, {floatPeak}).");
                // Exercise the actual retirement blur on the same scaled HDR glyphs.
                typeof(RenderLyricsLine).GetProperty(nameof(RenderLyricsLine.IsWordEffectsRetiring))!.SetValue(lines[0], true);
                lines[0].BlurAmountTransition.JumpTo(1.5);
                float blurredDefault = DrawScaled(false);
                float blurredFloat = DrawScaled(true);
                if (blurredFloat <= 2.25f || blurredFloat > Hdr.White + 0.015f || Math.Abs(blurredDefault - blurredFloat) > 0.025f)
                    throw new InvalidOperationException($"144-DPI lyrics lost highlight during scaled retirement blur ({blurredDefault}, {blurredFloat}).");
                return new
                {
                    Scale = 1.25, DefaultPrecision = defaultPeak, FloatPrecision = floatPeak,
                    BlurDip = 1.5, BlurredDefault = blurredDefault, BlurredFloat = blurredFloat
                };
            }
            finally { TimeProgressBus.SetClock(null); }

            float DrawScaled(bool floatEffects)
            {
                using (var drawing = lyricsTarget.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Black);
                    drawing.EffectBufferPrecision = floatEffects ? CanvasBufferPrecision.Precision16Float : null;
                    using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                    _lyrics.OnDraw(lyricsTarget, scope.Context, Hdr);
                }
                return ReadMax(lyricsTarget).Rgb;
            }
        }
    }
}
