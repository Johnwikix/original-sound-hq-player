using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

public sealed partial class AnimatedTextBlock
{
    private bool _isPointerOver;
    private bool _hoverChecked;
    private CanvasTextLayout _hoverTextLayout;
    private double _hoverElapsed;
    private float _hoverDistance;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Measure the template first so style setters have been applied. CanvasControl
        // has no intrinsic text size; use the same DirectWrite format as rendering.
        base.MeasureOverride(availableSize);
        ApplyTextFormatIfNeeded();
        double insetWidth = Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;
        double insetHeight = Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom;
        float width = double.IsPositiveInfinity(availableSize.Width)
            ? float.MaxValue : (float)Math.Max(0, availableSize.Width - insetWidth);
        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), Text ?? string.Empty,
            _textFormat, width, float.MaxValue);
        layout.TrimmingGranularity = CanvasTextTrimmingGranularity.None;
        layout.TrimmingSign = CanvasTrimmingSign.None;
        layout.HorizontalAlignment = CanvasHorizontalAlignment.Left;
        layout.VerticalAlignment = CanvasVerticalAlignment.Top;
        var bounds = layout.LayoutBounds;
        return new Size(
            Math.Min(availableSize.Width, Math.Ceiling(bounds.Width) + insetWidth),
            Math.Min(availableSize.Height, Math.Ceiling(bounds.Height) + insetHeight));
    }

    private void EnsureTextFormat()
    {
        if (_textFormat != null) return;
        _textFormat = new CanvasTextFormat { TrimmingSign = CanvasTrimmingSign.Ellipsis };
        _textFormatDirty = true;
    }

    private void AttachCanvas()
    {
        if (_canvas == null) return;
        DetachCanvas();
        _canvas.CreateResources += Canvas_CreateResources;
        _canvas.Draw += Canvas_Draw;
        _canvas.SizeChanged += OnSizeChanged;
    }

    private void DetachCanvas()
    {
        if (_canvas == null) return;
        _canvas.CreateResources -= Canvas_CreateResources;
        _canvas.Draw -= Canvas_Draw;
        _canvas.SizeChanged -= OnSizeChanged;
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AnimatedTextBlock)sender;
        control._oldText = control._newText;
        control._newText = (string)args.NewValue ?? string.Empty;
        control._staticLayoutDirty = true;
        control.InvalidateMeasure();
        control.SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    private static void OnTextEffectChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AnimatedTextBlock)sender;
        control._textEffect = (ITextEffect)args.NewValue;
        control.SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
    }

    private static void OnTextLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AnimatedTextBlock)sender;
        control._textAlignment = control.TextAlignment;
        control._textDirection = control.TextDirection;
        control._textTrimming = control.TextTrimming;
        control._textWrapping = control.TextWrapping;
        control._textFormatDirty = true;
        control.OnTextMetricsChanged(sender, null);
    }

    private void OnTextMetricsChanged(DependencyObject sender, DependencyProperty property)
    {
        StopHoverScroll();
        _staticLayoutDirty = true;
        InvalidateMeasure();
        ApplyTextFormatIfNeeded();
        RebuildLayoutsIfReady();
        _canvas?.Invalidate();
    }

    private static void OnHoverScrollEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AnimatedTextBlock)sender;
        control.StopHoverScroll();
        control._canvas?.Invalidate();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        _isPointerOver = true;
        _canvas?.Invalidate();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _isPointerOver = false;
        StopHoverScroll();
        _canvas?.Invalidate();
    }

    private void StopHoverScroll()
    {
        _hoverTextLayout?.Dispose();
        _hoverTextLayout = null;
        _hoverChecked = false;
        _hoverElapsed = 0;
        _hoverDistance = 0;
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
            StopRenderingLoop();
    }

    private void EnsureHoverScroll(CanvasControl sender)
    {
        // Horizontal marquee is defined for single-line text only. Transitions own
        // the trimmed layouts; the untrimmed layout exists only while idle + hovered.
        if (_hoverChecked || !IsLoaded || !IsHoverScrollEnabled || !_isPointerOver
            || _currentState != AnimatedTextBlockRedrawState.Idle
            || TextTrimming == TextTrimming.None || TextWrapping != TextWrapping.NoWrap
            || TextDirection > AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop)
            return;

        _hoverChecked = true;
        var lines = _staticTextLayout.LineMetrics;
        if (lines.Length != 1 || !lines[0].IsTrimmed) return;

        var layout = new CanvasTextLayout(sender, _newText, _textFormat, 0, (float)sender.Size.Height);
        layout.TrimmingGranularity = CanvasTextTrimmingGranularity.None;
        layout.TrimmingSign = CanvasTrimmingSign.None;
        layout.HorizontalAlignment = CanvasHorizontalAlignment.Left;
        float width = (float)Math.Ceiling(layout.LayoutBounds.Width);
        if (width <= sender.Size.Width)
        {
            layout.Dispose();
            return;
        }
        layout.RequestedSize = new Size(width, sender.Size.Height);
        _hoverTextLayout = layout;
        _hoverDistance = width - (float)sender.Size.Width;
        StartRenderingLoop();
    }

    private float GetHoverOffset()
    {
        if (_hoverTextLayout == null) return 0;
        const double pauseSeconds = 0.8;
        const double pixelsPerSecond = 36;
        double travelSeconds = _hoverDistance / pixelsPerSecond;
        double phase = _hoverElapsed % (2 * (pauseSeconds + travelSeconds));
        double progress = phase < pauseSeconds + travelSeconds
            ? Math.Clamp((phase - pauseSeconds) / travelSeconds, 0, 1)
            : 1 - Math.Clamp((phase - 2 * pauseSeconds - travelSeconds) / travelSeconds, 0, 1);
        bool rightToLeft = TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom
            || TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop;
        return (float)(-_hoverDistance * (rightToLeft ? 1 - progress : progress));
    }
}
