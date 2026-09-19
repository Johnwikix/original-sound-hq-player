using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

public sealed partial class AnimatedTextBlock
{
    private bool _isPointerOver;
    private bool _hoverChecked;
    private HoverLine[] _hoverLines;

    private readonly struct HoverLine(CanvasTextLayout layout, float y, float distance, float opacity, bool hasColorGlyphs)
    {
        public CanvasTextLayout Layout { get; } = layout;
        public float Y { get; } = y;
        public float Distance { get; } = distance;
        public float Opacity { get; } = opacity;
        public bool HasColorGlyphs { get; } = hasColorGlyphs;
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
        if (Document != null)
        {
            var size = MeasureDocument(width);
            return new Size(Math.Min(availableSize.Width, size.Width + insetWidth),
                Math.Min(availableSize.Height, size.Height + insetHeight));
        }
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

    private static void ApplyLineSpacing(CanvasTextFormat format, double lineHeight)
    {
        // Win2D's negative spacing sentinel restores natural font metrics.
        format.LineSpacing = -1;
        format.LineSpacingBaseline = 0;
        format.LineSpacingMode = CanvasLineSpacingMode.Default;
        if (!double.IsFinite(lineHeight) || lineHeight <= 0 || lineHeight > float.MaxValue)
            return;

        // A space uses the primary font, avoiding content-dependent fallback metrics.
        // Keep its baseline centered in the chosen line box for every script.
        using var reference = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), " ", format, 0, 0);
        var metric = reference.LineMetrics[0];
        format.LineSpacing = (float)lineHeight;
        format.LineSpacingBaseline = metric.Baseline + ((float)lineHeight - metric.Height) / 2;
        format.LineSpacingMode = CanvasLineSpacingMode.Uniform;
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

    private bool CanPrepareHoverScroll() => !_hoverChecked && IsLoaded && IsHoverScrollEnabled && _isPointerOver
        && _currentState == AnimatedTextBlockRedrawState.Idle
        && TextTrimming != TextTrimming.None && TextWrapping == TextWrapping.NoWrap
        && TextDirection <= AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop;

    private void EnsureHoverScroll(CanvasControl sender)
    {
        if (!CanPrepareHoverScroll()) return;
        _hoverChecked = true;
        if (!HasTrimmedLine(_staticTextLayout)) return;
        var lines = new List<HoverLine>();
        try
        {
            if (AppendHoverLines(sender, _staticTextLayout, _textFormat, _newText, 0, 1, lines))
            {
                _hoverLines = lines.ToArray();
                StartRenderingLoop();
            }
        }
        finally
        {
            if (_hoverLines == null)
                foreach (var line in lines) line.Layout.Dispose();
        }
    }

    private static bool HasTrimmedLine(CanvasTextLayout layout)
    {
        foreach (var metric in layout.LineMetrics)
            if (metric.IsTrimmed) return true;
        return false;
    }

    private bool AppendHoverLines(CanvasControl sender, CanvasTextLayout source, CanvasTextFormat format,
        string text, float offsetY, float opacity, List<HoverLine> lines)
    {
        var metrics = source.LineMetrics;
        bool bottomToTop = TextDirection == AnimatedTextBlockTextDirection.LeftToRightThenBottomToTop
            || TextDirection == AnimatedTextBlockTextDirection.RightToLeftThenBottomToTop;
        float y = offsetY + (float)source.LayoutBounds.Y;
        if (bottomToTop)
            foreach (var metric in metrics) y += metric.Height;
        int textOffset = 0;
        bool scrollable = false;
        foreach (var metric in metrics)
        {
            if (bottomToTop) y -= metric.Height;
            string lineText = text.Substring(textOffset, metric.CharacterCount - metric.TerminalNewlineCount);
            var layout = new CanvasTextLayout(sender, lineText, format, (float)sender.Size.Width, metric.Height);
            try
            {
                layout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
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
                float baseline = layout.LineMetrics[0].Baseline;
                lines.Add(new HoverLine(layout, y + metric.Baseline - baseline, distance, opacity, ShapedText.MayContainColorGlyphs(lineText)));
                scrollable |= distance > 0;
            }
            catch
            {
                layout.Dispose();
                throw;
            }
            textOffset += metric.CharacterCount;
            if (!bottomToTop) y += metric.Height;
        }
        return scrollable;
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
