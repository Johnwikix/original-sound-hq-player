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
    private HoverLine[] _hoverLines;

    private readonly struct HoverLine(CanvasTextLayout layout, float y, float distance)
    {
        public CanvasTextLayout Layout { get; } = layout;
        public float Y { get; } = y;
        public float Distance { get; } = distance;
    }
    private double _hoverElapsed;

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

    private void ApplyLineSpacing()
    {
        // Win2D's negative spacing sentinel restores natural font metrics.
        _textFormat.LineSpacing = -1;
        _textFormat.LineSpacingBaseline = 0;
        _textFormat.LineSpacingMode = CanvasLineSpacingMode.Default;
        if (!double.IsFinite(LineHeight) || LineHeight <= 0 || LineHeight > float.MaxValue)
            return;

        // A space uses the primary font, avoiding content-dependent fallback metrics.
        // Keep its baseline centered in the chosen line box for every script.
        using var reference = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), " ", _textFormat, 0, 0);
        var metric = reference.LineMetrics[0];
        _textFormat.LineSpacing = (float)LineHeight;
        _textFormat.LineSpacingBaseline = metric.Baseline + ((float)LineHeight - metric.Height) / 2;
        _textFormat.LineSpacingMode = CanvasLineSpacingMode.Uniform;
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
        if (_hoverLines != null)
        {
            foreach (var line in _hoverLines)
                line.Layout.Dispose();
            _hoverLines = null;
        }
        _hoverChecked = false;
        _hoverElapsed = 0;
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
            StopRenderingLoop();
    }

    private void EnsureHoverScroll(CanvasControl sender)
    {
        // NoWrap still allows explicit newlines (e.g. album + artist). Each line
        // retains its baseline and alignment; only actually trimmed lines move.
        // Transitions continue to own the original, complete text layouts.
        if (_hoverChecked || !IsLoaded || !IsHoverScrollEnabled || !_isPointerOver
            || _currentState != AnimatedTextBlockRedrawState.Idle
            || TextTrimming == TextTrimming.None || TextWrapping != TextWrapping.NoWrap
            || TextDirection > AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop)
            return;

        _hoverChecked = true;
        var metrics = _staticTextLayout.LineMetrics;
        bool hasTrimmedLine = false;
        foreach (var metric in metrics)
            hasTrimmedLine |= metric.IsTrimmed;
        if (!hasTrimmedLine) return;

        var lines = new HoverLine[metrics.Length];
        bool bottomToTop = TextDirection == AnimatedTextBlockTextDirection.LeftToRightThenBottomToTop
            || TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop;
        float y = (float)_staticTextLayout.LayoutBounds.Y;
        if (bottomToTop)
        {
            foreach (var metric in metrics)
                y += metric.Height;
        }
        int textOffset = 0;
        bool hasScrollableLine = false;
        try
        {
            for (int i = 0; i < metrics.Length; i++)
            {
                var metric = metrics[i];
                if (bottomToTop) y -= metric.Height;
                // DirectWrite counts UTF-16 code units, including CRLF as two units.
                string text = _newText.Substring(textOffset, metric.CharacterCount - metric.TerminalNewlineCount);
                lines[i] = CreateHoverLine(sender, text, metric, y);
                hasScrollableLine |= lines[i].Distance > 0;
                textOffset += metric.CharacterCount;
                if (!bottomToTop) y += metric.Height;
            }
            if (hasScrollableLine)
            {
                _hoverLines = lines;
                StartRenderingLoop();
            }
        }
        finally
        {
            if (_hoverLines != lines)
            {
                foreach (var line in lines)
                    line.Layout?.Dispose();
            }
        }
    }

    private HoverLine CreateHoverLine(CanvasControl sender, string text, CanvasLineMetrics metric, float y)
    {
        var layout = new CanvasTextLayout(sender, text, _textFormat, (float)sender.Size.Width, metric.Height);
        try
        {
            layout.TrimmingGranularity = CanvasTextTrimmingGranularity.None;
            layout.TrimmingSign = CanvasTrimmingSign.None;
            layout.VerticalAlignment = CanvasVerticalAlignment.Top;
            float width = (float)Math.Ceiling(layout.LayoutBounds.Width);
            float distance = metric.IsTrimmed ? Math.Max(0, width - (float)sender.Size.Width) : 0;
            if (distance > 0)
            {
                layout.HorizontalAlignment = CanvasHorizontalAlignment.Left;
                layout.RequestedSize = new Size(width, metric.Height);
            }
            // Emoji/fallback fonts can give individual lines different ascenders.
            // Match the original baseline rather than centering each line separately.
            float baseline = layout.LineMetrics[0].Baseline;
            return new HoverLine(layout, y + metric.Baseline - baseline, distance);
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    private float GetHoverOffset(float distance)
    {
        if (distance <= 0) return 0;
        const double pauseSeconds = 0.8;
        const double pixelsPerSecond = 36;
        double travelSeconds = distance / pixelsPerSecond;
        double phase = _hoverElapsed % (2 * (pauseSeconds + travelSeconds));
        double progress = phase < pauseSeconds + travelSeconds
            ? Math.Clamp((phase - pauseSeconds) / travelSeconds, 0, 1)
            : 1 - Math.Clamp((phase - 2 * pauseSeconds - travelSeconds) / travelSeconds, 0, 1);
        bool rightToLeft = TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenTopToBottom
            || TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop;
        return (float)(-distance * (rightToLeft ? 1 - progress : progress));
    }
}
