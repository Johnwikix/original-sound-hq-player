#nullable enable
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;

public sealed partial class AdvanceLyricsCanvasControl
{
    public static readonly DependencyProperty UseTransparentPresentationProperty =
        DependencyProperty.Register(nameof(UseTransparentPresentation), typeof(bool), typeof(AdvanceLyricsCanvasControl),
            new PropertyMetadata(false, OnTransparentPresentationChanged));

    public bool UseTransparentPresentation
    {
        get => (bool)GetValue(UseTransparentPresentationProperty);
        set => SetValue(UseTransparentPresentationProperty, value);
    }

    private Image? _transparentCanvas;
    private CanvasImageSource? _transparentSource;
    private CanvasDevice? _transparentDevice;
    private Size _transparentSize;
    private float _transparentDpi;
    private volatile bool _useTransparentPresentation;
    private FrameEventSubscription? _transparentClock;
    private PresentationFrameClock _transparentFrameClock;

    private bool CanRender => !_shutdown && _isActive && !_pausedByVisibility && !_pausedByParent && !_pausedByWindow;

    private static void OnTransparentPresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AdvanceLyricsCanvasControl)sender;
        control._useTransparentPresentation = (bool)args.NewValue;
        control.ApplyPresentationMode();
    }

    private void ApplyPresentationMode()
    {
        _coordinator.TransparentInput = _useTransparentPresentation ? _transparentCanvas : null;
        if (_canvas is { } animated)
        {
            animated.Visibility = _useTransparentPresentation ? Visibility.Collapsed : Visibility.Visible;
            if (_useTransparentPresentation)
                DetachFrameEvents();
            else if (IsLoaded)
                AttachFrameEvents();
        }
        if (_transparentCanvas is { } transparent)
            transparent.Visibility = _useTransparentPresentation ? Visibility.Visible : Visibility.Collapsed;

        _coordinator.OnSizeChanged();
        UpdateCanvasPaused();
    }

    private void UpdateTransparentClock()
    {
        if (!IsLoaded || !CanRender || !_useTransparentPresentation || _transparentCanvas is null)
        {
            StopTransparentClock();
            ResetTransparentSurface();
            return;
        }

        if (_transparentClock is null)
        {
            _transparentFrameClock = default;
            _transparentClock = FrameEventSubscription.SubscribeRendering(DrawTransparentFrame);
            CompositionTarget.SurfaceContentsLost += OnSurfaceContentsLost;
        }
    }

    private void StopTransparentClock()
    {
        if (_transparentClock is null) return;
        CompositionTarget.SurfaceContentsLost -= OnSurfaceContentsLost;
        _transparentClock.Dispose();
        _transparentClock = null;
    }

    private void OnSurfaceContentsLost(object? sender, object args) => ResetTransparentSurface();

    private void ResetTransparentSurface()
    {
        if (_transparentCanvas is { } image) image.Source = null;
        _transparentSource = null;
    }

    private void DrawTransparentFrame()
    {
        if (!CanRender || !_useTransparentPresentation || _transparentCanvas is not { XamlRoot: { } root } image
            || !_transparentFrameClock.TryAdvance(Stopwatch.GetTimestamp(), _coordinator.TargetFrameRate, false, out var elapsed))
            return;

        try
        {
            // An Image without a source measures 0x0. The control already owns the lyric slot.
            var size = new Size(ActualWidth, ActualHeight);
            float dpi = (float)(96 * root.RasterizationScale);
            if (size.Width * dpi < 96 || size.Height * dpi < 96) return;
            var device = _transparentDevice ??= CanvasDevice.GetSharedDevice();
            bool replace = _transparentSource is null || size != _transparentSize || dpi != _transparentDpi;
            var surface = replace
                ? new CanvasImageSource(device, (float)size.Width, (float)size.Height, dpi, CanvasAlphaMode.Premultiplied)
                : _transparentSource!;
            if (replace) _coordinator.OnCreateResources();
            _coordinator.OnUpdate(device, elapsed > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : elapsed);
            using (var drawing = surface.CreateDrawingSession(Colors.Transparent))
            using (var session = CanvasDrawingSessionScope.Borrow(drawing))
                _coordinator.OnDraw(device, session.Context, consumePendingLyrics: false);
            if (replace)
            {
                image.Source = surface;
                _transparentSource = surface;
                _transparentSize = size;
                _transparentDpi = dpi;
            }
        }
        catch (Exception ex) when (_transparentDevice?.IsDeviceLost(ex.HResult) == true)
        {
            ResetTransparentSurface();
            _transparentDevice = null;
            _coordinator.ResetDeviceResources();
        }
        catch (Exception ex) { _coordinator.RaiseRenderError(ex); }
    }

    private void ReleaseTransparentCanvas()
    {
        StopTransparentClock();
        ResetTransparentSurface();
        _transparentDevice = null;
        if (_transparentCanvas is not null) DetachPointerEvents(_transparentCanvas);
        _transparentCanvas = null;
        _coordinator.TransparentInput = null;
    }
}
