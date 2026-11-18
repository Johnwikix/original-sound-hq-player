#nullable enable
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Shaders;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
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
    private bool _preparingStandalone;
    private bool _standaloneFramePending;

    internal bool IsStandaloneFramePending => _standaloneFramePending;

    internal void PrepareStandalonePresentation()
    {
        if (_scenePresentation && _canvas is not null)
            UpdateScenePresentation(false, prepareStandalone: true);
    }

    internal void SetScenePresentation(bool enabled) => UpdateScenePresentation(enabled, prepareStandalone: false);

    private void UpdateScenePresentation(bool enabled, bool prepareStandalone)
    {
        if (_scenePresentation == enabled && _preparingStandalone == prepareStandalone
            && (enabled || _sceneTarget is null)) return;
        _scenePresentation = enabled;
        _preparingStandalone = prepareStandalone;
        _standaloneFramePending = prepareStandalone;
        if (_canvas is not null)
            _canvas.Opacity = enabled || prepareStandalone ? 0 : 1;
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

    internal bool DrawInScene(CanvasDrawingSession presentation, PlaybackCanvasFrame frame, Rect bounds, TimeSpan elapsed)
    {
        if (!IsLoaded || !_isActive || Document is null || bounds.Width * frame.Dpi < 96 || bounds.Height * frame.Dpi < 96)
            return false;

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
        presentation.DrawImage(_sceneOutput, bounds, source);
        return true;
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
        _sceneOutput?.Dispose();
        _sceneOutput = null;
        _sceneTarget?.Dispose();
        _sceneTarget = null;
        _sceneDevice = null;
    }
}
