#nullable enable
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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

    private CanvasControl? _transparentCanvas;
    private volatile bool _useTransparentPresentation;
    private bool _transparentClockRegistered;
    private long _lastTransparentDraw;

    private bool CanRender => !_shutdown && _isActive && !_pausedByVisibility && !_pausedByParent && !_pausedByWindow;

    private static void OnTransparentPresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AdvanceLyricsCanvasControl)sender;
        control._useTransparentPresentation = (bool)args.NewValue;
        control.ApplyPresentationMode();
    }

    private void ApplyPresentationMode()
    {
        _coordinator.TransparentCanvas = _useTransparentPresentation ? _transparentCanvas : null;
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
            return;
        }

        if (!_transparentClockRegistered)
        {
            _lastTransparentDraw = 0;
            CompositionTarget.Rendering += InvalidateTransparentCanvas;
            _transparentClockRegistered = true;
            _transparentCanvas.Invalidate();
        }
    }

    private void StopTransparentClock()
    {
        if (!_transparentClockRegistered) return;
        CompositionTarget.Rendering -= InvalidateTransparentCanvas;
        _transparentClockRegistered = false;
    }

    private void InvalidateTransparentCanvas(object? sender, object args)
    {
        if (_lastTransparentDraw == 0 || Stopwatch.GetElapsedTime(_lastTransparentDraw).TotalSeconds
            >= 1d / _coordinator.TargetFrameRate - 0.002)
            _transparentCanvas?.Invalidate();
    }

    private void TransparentCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_useTransparentPresentation)
            _coordinator.OnCreateResources();
    }

    private void TransparentCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!CanRender || !_useTransparentPresentation) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = _lastTransparentDraw == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastTransparentDraw, now);
            _lastTransparentDraw = now;
            _coordinator.OnUpdate(sender, elapsed > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : elapsed);
            using var session = CanvasDrawingSessionScope.Borrow(args.DrawingSession);
            session.Context.Clear(Colors.Transparent);
            _coordinator.OnDraw(sender, session.Context, consumePendingLyrics: false);
        }
        catch (Exception ex) { _coordinator.RaiseRenderError(ex); }
    }

    private void ReleaseTransparentCanvas()
    {
        StopTransparentClock();
        if (_transparentCanvas is null) return;
        _transparentCanvas.CreateResources -= TransparentCanvas_CreateResources;
        _transparentCanvas.Draw -= TransparentCanvas_Draw;
        DetachPointerEvents(_transparentCanvas);
        _transparentCanvas.RemoveFromVisualTree();
        _transparentCanvas = null;
        _coordinator.TransparentCanvas = null;
    }
}
