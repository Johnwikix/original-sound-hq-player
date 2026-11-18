#nullable enable
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Shaders;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using System;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

public sealed partial class AnimatedTextBlock
{
    private bool _scenePresentation;
    private bool _sceneTargetDirty;
    private CanvasRenderTarget? _sceneTarget;
    private CanvasDevice? _sceneDevice;
    private Rect _sceneRect;
    private PixelShaderEffect<ScRgbPlaybackEffect>? _sceneOutput;
    private CropEffect? _sceneTitleCrop;
    private GaussianBlurEffect? _sceneTitleBlur;
    private PixelShaderEffect<PlaybackTextHaloEffect>? _sceneTitleHalo;
    private PixelShaderEffect<PlaybackTextGlowCoreEffect>? _sceneTitleGlow;

    internal void SetScenePresentation(bool enabled)
    {
        if (_scenePresentation == enabled) return;
        _scenePresentation = enabled;
        if (_canvas is not null)
            _canvas.Opacity = enabled ? 0 : 1;
        if (enabled)
        {
            StopRenderingLoop();
        }
        else
        {
            ReleaseSceneDeviceLayouts(_canvas is { ReadyToDraw: true } canvas ? canvas.Device : null);
            ReleaseSceneTarget();
            if (_currentState != AnimatedTextBlockRedrawState.Idle || _hoverLines is not null)
                StartRenderingLoop();
            InvalidateCanvas();
        }
    }

    private void InvalidateCanvas()
    {
        if (_scenePresentation)
            _sceneTargetDirty = true;
        else
            _canvas?.Invalidate();
    }

    internal void DrawInScene(CanvasDrawingSession presentation, PlaybackCanvasFrame frame, Rect bounds, TimeSpan elapsed)
    {
        if (!IsLoaded || !_isActive || Document is null || bounds.Width * frame.Dpi < 96 || bounds.Height * frame.Dpi < 96)
            return;

        bool replace = _sceneTarget is null || !ReferenceEquals(_sceneDevice, frame.Device)
            || _sceneRect.Width != bounds.Width || _sceneRect.Height != bounds.Height || _sceneTarget.Dpi != frame.Dpi;
        if (replace)
        {
            bool deviceChanged = !ReferenceEquals(_documentDevice, frame.Device);
            ReleaseSceneDeviceLayouts(frame.Device);
            ReleaseSceneTarget();
            _sceneTarget = new CanvasRenderTarget(frame.Device, (float)bounds.Width, (float)bounds.Height, frame.Dpi);
            _sceneDevice = frame.Device;
            _sceneRect = bounds;
            _sceneOutput = new PixelShaderEffect<ScRgbPlaybackEffect>();
            _sceneOutput.Sources[0] = _sceneTarget;
            _sceneTargetDirty = true;
            if (deviceChanged)
                _documentLayoutDirty = true;
        }

        AdvanceAnimation(elapsed);
        if (_sceneTargetDirty || _documentLayoutDirty || _currentState != AnimatedTextBlockRedrawState.Idle || _hoverLines is not null)
        {
            using var tile = _sceneTarget!.CreateDrawingSession();
            tile.Clear(Colors.Transparent);
            ApplyTextFormatIfNeeded();
            DrawDocument(frame.Device, bounds.Width, tile);
            _sceneTargetDirty = false;
        }

        _sceneOutput!.ConstantBuffer = new ScRgbPlaybackEffect(frame.Hdr.White);
        var source = new Rect(0, 0, bounds.Width, bounds.Height);
        float glow = GetSceneTitleGlow();
        if (glow <= 0f || _documentLayouts is not { Length: > 0 })
        {
            presentation.DrawImage(_sceneOutput, bounds, source);
            return;
        }

        double titleHeight = Math.Clamp(_documentLayouts[0].NewLayout.RequestedSize.Height, 0, bounds.Height);
        if (titleHeight <= 0)
        {
            presentation.DrawImage(_sceneOutput, bounds, source);
            return;
        }

        EnsureSceneTitleGlow();
        var titleSource = new Rect(0, 0, bounds.Width, titleHeight);
        var titleDestination = new Rect(bounds.X, bounds.Y, bounds.Width, titleHeight);
        _sceneTitleCrop!.SourceRectangle = titleSource;
        _sceneTitleHalo!.ConstantBuffer = new PlaybackTextHaloEffect(frame.Hdr.White, frame.Hdr.TextGlowPeak, glow);
        _sceneTitleGlow!.ConstantBuffer = new PlaybackTextGlowCoreEffect(frame.Hdr.White, frame.Hdr.TextGlowPeak, glow);
        presentation.DrawImage(_sceneTitleHalo, titleDestination, titleSource);
        presentation.DrawImage(_sceneTitleGlow, titleDestination, titleSource);
        if (titleHeight < bounds.Height)
        {
            presentation.DrawImage(_sceneOutput, new Rect(bounds.X, bounds.Y + titleHeight, bounds.Width, bounds.Height - titleHeight),
                new Rect(0, titleHeight, bounds.Width, bounds.Height - titleHeight));
        }
    }

    private float GetSceneTitleGlow()
    {
        if (_currentState != AnimatedTextBlockRedrawState.Animating || _textEffect is null)
            return 0f;

        double duration = _textEffect.AnimationDuration.TotalSeconds;
        if (duration <= 0)
            return 0f;
        double progress = (_totalAnimationTime - _animationBeginTime).TotalSeconds / duration;
        return (float)(Math.Clamp(progress / 0.18, 0d, 1d) * Math.Clamp((0.95 - progress) / 0.28, 0d, 1d));
    }

    private void EnsureSceneTitleGlow()
    {
        if (_sceneTitleGlow is not null)
            return;

        _sceneTitleCrop = new CropEffect { Source = _sceneOutput, BufferPrecision = CanvasBufferPrecision.Precision16Float };
        _sceneTitleBlur = new GaussianBlurEffect
        {
            Source = _sceneTitleCrop, BlurAmount = 3f, BorderMode = EffectBorderMode.Soft,
            BufferPrecision = CanvasBufferPrecision.Precision16Float
        };
        _sceneTitleHalo = new PixelShaderEffect<PlaybackTextHaloEffect>();
        _sceneTitleHalo.Sources[0] = _sceneTitleBlur;
        _sceneTitleGlow = new PixelShaderEffect<PlaybackTextGlowCoreEffect>();
        _sceneTitleGlow.Sources[0] = _sceneTitleCrop;
    }

    private void ReleaseSceneDeviceLayouts(CanvasDevice? replacement)
    {
        if (_sceneDevice is not { } device || ReferenceEquals(device, replacement) || !ReferenceEquals(device, _documentDevice))
            return;

        StopHoverScroll();
        DisposeDocumentLayouts();
        TextRenderingHelper.DisposeShapedText(_diffResults);
        _diffResults = null;
        _textBrush?.Dispose();
        _textBrush = null;
        _documentLayoutDirty = true;
    }

    private void ReleaseSceneTarget()
    {
        _sceneTitleGlow?.Dispose();
        _sceneTitleGlow = null;
        _sceneTitleHalo?.Dispose();
        _sceneTitleHalo = null;
        _sceneTitleBlur?.Dispose();
        _sceneTitleBlur = null;
        _sceneTitleCrop?.Dispose();
        _sceneTitleCrop = null;
        _sceneOutput?.Dispose();
        _sceneOutput = null;
        _sceneTarget?.Dispose();
        _sceneTarget = null;
        _sceneDevice = null;
    }
}
