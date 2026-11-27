#nullable enable
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Shaders;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace AnimatedWin2dControls.Controls;

public sealed partial class NowPlayingCanvas
{
    // At the default 50% waiting opacity this gives an SDR ink lift of 90 code values.
    private const float SdrLyricsLift = 180f / 255f;

    private CanvasRenderTarget? _lyricsBackdropMask;
    private CanvasRenderTarget? _lyricsBackdropPatch;
    private (int Width, int Height, float Dpi) _lyricsBackdropMaskSize;
    private (int Width, int Height, float Dpi) _lyricsBackdropPatchSize;
    private long _lyricsBackdropPatchFrame;
    private Transform2DEffect? _lyricsBackdropOrigin;
    private PixelShaderEffect<PlaybackLyricsBackdropEffect>? _lyricsBackdropEffect;
    private CanvasRenderTarget? _lyricsBackdropEffectBackground;
    private CanvasRenderTarget? _lyricsBackdropEffectMask;
    private Vector2 _lyricsBackdropOriginOffset;
    private float _lyricsBackdropWhite;
    private float _lyricsBackdropOutlineRadius;
    private (Size Size, float Dpi)? _lyricsBackdropFailure;

    private bool TryPrepareLyricsBackdrop(PlaybackCanvasFrame frame, out Rect bounds, out bool pendingLyricsHandled)
    {
        bounds = default;
        pendingLyricsHandled = false;

        if (!_advanced || !_isDark || _useImageBackground || !_coordinator.CanDrawBackdropTint)
        {
            ReleaseLyricsBackdropResources();
            _lyricsBackdropFailure = null;
            return false;
        }

        if (_lyricsBackdropFailure == (frame.Size, frame.Dpi)) return false;
        if (!_coordinator.TryGetBackdropTintBounds(out var region))
        {
            ReleaseLyricsBackdropResources();
            return false;
        }

        bounds = GetBackdropMaskBounds(region, frame);
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        try
        {
            var mask = EnsureBackdropTarget(ref _lyricsBackdropMask, ref _lyricsBackdropMaskSize,
                frame, bounds, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            using var drawing = CanvasDrawingSessionScope.Create(mask);
            var context = drawing.Context;
            context.Clear(Colors.Transparent);
            context.Transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y);

            pendingLyricsHandled = !frame.Hdr.IsEnabled;
            return _coordinator.DrawBackdropTintMask(frame.Device, context,
                consumePendingLyrics: pendingLyricsHandled, smoothSweep: frame.Hdr.IsEnabled);
        }
        catch (Exception ex) when (!frame.Device.IsDeviceLost(ex.HResult))
        {
            DisableLyricsBackdropForFrameSize(frame, ex);
            return false;
        }
    }

    private static Rect GetBackdropMaskBounds(Rect region, PlaybackCanvasFrame frame)
    {
        double scale = frame.Dpi / 96d;
        // Reserve room for the HDR outline and align the patch to the target's pixel grid.
        double pad = frame.Hdr.IsEnabled ? 2d : 0d;
        double left = Math.Max(0, Math.Floor((region.X - pad) * scale));
        double top = Math.Max(0, Math.Floor((region.Y - pad) * scale));
        double right = Math.Min(Math.Ceiling(frame.Size.Width * scale), Math.Ceiling((region.Right + pad) * scale));
        double bottom = Math.Min(Math.Ceiling(frame.Size.Height * scale), Math.Ceiling((region.Bottom + pad) * scale));
        return new Rect(left / scale, top / scale, Math.Max(0, right - left) / scale, Math.Max(0, bottom - top) / scale);
    }

    private static CanvasRenderTarget EnsureBackdropTarget(ref CanvasRenderTarget? target,
        ref (int Width, int Height, float Dpi) currentSize, PlaybackCanvasFrame frame,
        Rect bounds, DirectXPixelFormat format)
    {
        var size = (frame.ConvertDipsToPixels((float)bounds.Width, CanvasDpiRounding.Round),
            frame.ConvertDipsToPixels((float)bounds.Height, CanvasDpiRounding.Round), frame.Dpi);
        if (target is { } current && currentSize == size) return current;

        var replacement = new CanvasRenderTarget(frame.Device, (float)bounds.Width, (float)bounds.Height,
            frame.Dpi, format, CanvasAlphaMode.Premultiplied);
        target?.Dispose();
        target = replacement;
        currentSize = size;
        return replacement;
    }

    private bool DrawSpectrumAndLyricsBackdrop(PlaybackCanvasFrame frame, CanvasDrawContext scene, bool tint, Rect bounds,
        out bool hoverDrawn)
    {
        hoverDrawn = false;
        if (!tint || (frame.Hdr.IsEnabled && _bgCache is null))
        {
            DrawSpectrum(frame, scene);
            return false;
        }

        bool spectrumDrawn = false;
        try
        {
            var local = new Rect(0, 0, bounds.Width, bounds.Height);
            var mask = _lyricsBackdropMask!;
            bool hover = _coordinator.HasBackdropTintHover;
            CanvasRenderTarget? patch = null;

            if (frame.Hdr.IsEnabled && (_spectrum?.HasEnergy == true || hover))
            {
                // Sample the same spectrum and hover behind the lyrics. Mirror only their
                // draw calls into this cropped patch and reuse the existing FP16 backdrop.
                patch = EnsureBackdropTarget(ref _lyricsBackdropPatch, ref _lyricsBackdropPatchSize,
                    frame, bounds, DirectXPixelFormat.R16G16B16A16Float);
                _lyricsBackdropPatchFrame = _frameCounter;
                using var drawing = CanvasDrawingSessionScope.Create(patch);
                var backdrop = drawing.Context;
                backdrop.Clear(Colors.Black);
                backdrop.DrawImage(_bgCache!, local, bounds);
                backdrop.Transform = Matrix3x2.CreateTranslation(-(float)bounds.X, -(float)bounds.Y);
                DrawSpectrum(frame, scene, backdrop);
                spectrumDrawn = true;
                if (hover)
                {
                    _coordinator.DrawBackdropTintHover(frame.Device, scene, frame.Hdr);
                    hoverDrawn = true;
                    _coordinator.DrawBackdropTintHover(frame.Device, backdrop, frame.Hdr);
                }
            }
            else
            {
                DrawSpectrum(frame, scene);
                spectrumDrawn = true;
                if (hover)
                {
                    _coordinator.DrawBackdropTintHover(frame.Device, scene, frame.Hdr);
                    hoverDrawn = true;
                }
            }

            if (frame.Hdr.IsEnabled)
                DrawHdrLyricsBackdrop(frame, scene, mask, patch, bounds, local);
            else
                scene.DrawImage(mask, bounds, local, SdrLyricsLift, CanvasImageInterpolation.Linear, CanvasComposite.Add);

            return true;
        }
        catch (Exception ex) when (!frame.Device.IsDeviceLost(ex.HResult))
        {
            DisableLyricsBackdropForFrameSize(frame, ex);
            if (!spectrumDrawn) DrawSpectrum(frame, scene);
            return false;
        }
    }

    private void DrawHdrLyricsBackdrop(PlaybackCanvasFrame frame, CanvasDrawContext scene,
        CanvasRenderTarget mask, CanvasRenderTarget? patch, Rect bounds, Rect local)
    {
        var effect = _lyricsBackdropEffect ??= new PixelShaderEffect<PlaybackLyricsBackdropEffect>();
        float outlineRadius = 1.25f * frame.Dpi / 96f;
        if (_lyricsBackdropOutlineRadius != outlineRadius)
        {
            // Let D2D include the neighbors needed by the outline when it tiles the effect.
            effect.TransformMapper = D2D1DrawTransformMapper<PlaybackLyricsBackdropEffect>.Inflate((int)MathF.Ceiling(outlineRadius) + 1);
        }

        if (_lyricsBackdropWhite != frame.Hdr.White || _lyricsBackdropOutlineRadius != outlineRadius)
        {
            effect.ConstantBuffer = new PlaybackLyricsBackdropEffect(frame.Hdr.White, outlineRadius);
            _lyricsBackdropWhite = frame.Hdr.White;
            _lyricsBackdropOutlineRadius = outlineRadius;
        }

        var background = patch ?? _bgCache!;
        if (patch is null)
        {
            var origin = _lyricsBackdropOrigin ??= new Transform2DEffect();
            var offset = new Vector2(-(float)bounds.X, -(float)bounds.Y);
            if (_lyricsBackdropOriginOffset != offset)
            {
                origin.TransformMatrix = Matrix3x2.CreateTranslation(offset);
                _lyricsBackdropOriginOffset = offset;
            }

            if (!ReferenceEquals(_lyricsBackdropEffectBackground, background))
            {
                origin.Source = background;
                effect.Sources[0] = origin;
            }
        }
        else if (!ReferenceEquals(_lyricsBackdropEffectBackground, background))
        {
            effect.Sources[0] = patch;
        }

        _lyricsBackdropEffectBackground = background;
        if (!ReferenceEquals(_lyricsBackdropEffectMask, mask))
        {
            effect.Sources[1] = mask;
            _lyricsBackdropEffectMask = mask;
        }
        scene.DrawImage(effect, bounds, local);
    }

    private void ReleaseIdleLyricsBackdropPatch()
    {
        // A transient pointer gap should not cause the next hover to rebuild a texture.
        if (_lyricsBackdropPatch is not { } patch || _frameCounter - _lyricsBackdropPatchFrame < 60) return;

        if (ReferenceEquals(_lyricsBackdropEffectBackground, patch))
        {
            _lyricsBackdropEffect!.Sources[0] = null;
            _lyricsBackdropEffectBackground = null;
        }
        patch.Dispose();
        _lyricsBackdropPatch = null;
        _lyricsBackdropPatchSize = default;
    }

    private void DisableLyricsBackdropForFrameSize(PlaybackCanvasFrame frame, Exception error)
    {
#if DEBUG
        Serilog.Log.ForContext<NowPlayingCanvas>().Debug(error, "Lyric backdrop effect fell back to normal text");
#endif
        ReleaseLyricsBackdropResources();
        _lyricsBackdropFailure = (frame.Size, frame.Dpi);
    }

    private void ReleaseLyricsBackdropResources()
    {
        _lyricsBackdropEffect?.Dispose();
        _lyricsBackdropEffect = null;
        _lyricsBackdropEffectBackground = null;
        _lyricsBackdropEffectMask = null;
        _lyricsBackdropOrigin?.Dispose();
        _lyricsBackdropOrigin = null;
        _lyricsBackdropOriginOffset = default;
        _lyricsBackdropPatch?.Dispose();
        _lyricsBackdropPatch = null;
        _lyricsBackdropMask?.Dispose();
        _lyricsBackdropMask = null;
        _lyricsBackdropMaskSize = default;
        _lyricsBackdropPatchSize = default;
        _lyricsBackdropWhite = 0;
        _lyricsBackdropOutlineRadius = 0;
    }
}
