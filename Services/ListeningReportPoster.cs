using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace WinUIMusicPlayer.Services;

internal sealed class ListeningReportPoster
{
    private delegate void Paint(CanvasDrawingSession drawing, CanvasBitmap? cover, CanvasBitmap? background);

    private readonly List<Paint> _layers = [];
    private readonly float _scaleX;
    private readonly float _scaleY;

    private ListeningReportPoster(Grid poster)
    {
        _scaleX = (float)(ListeningReportImages.Width / poster.ActualWidth);
        _scaleY = (float)(ListeningReportImages.Height / poster.ActualHeight);
        Collect(poster, poster, new Windows.UI.ViewManagement.UISettings().TextScaleFactor);
    }

    // 直接读取已排版海报的数据。后台绘制仅持有矩形、文本、颜色及两个软件位图，不持有 XAML 元素。
    internal static ListeningReportPoster Read(Grid poster) => new(poster);

    internal async Task<SoftwareBitmap> RenderAsync(SoftwareBitmap? cover, SoftwareBitmap? background, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, ListeningReportImages.Width, ListeningReportImages.Height, 96);
        using var coverImage = cover is null ? null : CanvasBitmap.CreateFromSoftwareBitmap(device, cover);
        using var backgroundImage = background is null ? null : CanvasBitmap.CreateFromSoftwareBitmap(device, background);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(default(Color));
            drawing.Transform = Matrix3x2.CreateScale(_scaleX, _scaleY);
            foreach (var layer in _layers)
            {
                token.ThrowIfCancellationRequested();
                layer(drawing, coverImage, backgroundImage);
            }
        }

        token.ThrowIfCancellationRequested();
        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(target, BitmapAlphaMode.Premultiplied)
            .AsTask().ConfigureAwait(false);
    }

    private void Collect(FrameworkElement element, Grid root, double textScale)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var bounds = element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        switch (element)
        {
            case Panel panel:
                if (panel.Background is LinearGradientBrush gradient)
                    AddGradient(gradient, bounds);
                foreach (var child in panel.Children)
                    if (child is FrameworkElement item) Collect(item, root, textScale);
                break;
            case Image { Source: not null }:
                _layers.Add((drawing, _, background) =>
                {
                    if (background is not null)
                        drawing.DrawImage(background, bounds, background.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                });
                break;
            case Border { Background: ImageBrush { ImageSource: not null } } border:
                float corner = (float)border.CornerRadius.TopLeft;
                _layers.Add((drawing, cover, _) => DrawCover(drawing, cover, bounds, corner));
                break;
            case TextBlock { Foreground: SolidColorBrush brush } text when text.Text.Length > 0:
                var run = new PosterText(text, bounds, brush.Color, textScale);
                _layers.Add((drawing, _, _) => run.Draw(drawing));
                break;
            case Ellipse ellipse:
                AddEllipse(ellipse, bounds);
                break;
            case Rectangle { Fill: SolidColorBrush fill }:
                Color color = fill.Color;
                _layers.Add((drawing, _, _) => drawing.FillRectangle(bounds, color));
                break;
        }
    }

    private void AddGradient(LinearGradientBrush gradient, Rect bounds)
    {
        CanvasGradientStop[] stops = new CanvasGradientStop[gradient.GradientStops.Count];
        for (int index = 0; index < stops.Length; index++)
        {
            var stop = gradient.GradientStops[index];
            stops[index] = new CanvasGradientStop { Position = (float)stop.Offset, Color = stop.Color };
        }

        Vector2 start = At(bounds, gradient.StartPoint);
        Vector2 end = At(bounds, gradient.EndPoint);
        _layers.Add((drawing, _, _) =>
        {
            using var brush = new CanvasLinearGradientBrush(drawing, stops) { StartPoint = start, EndPoint = end };
            drawing.FillRectangle(bounds, brush);
        });
    }

    private static Vector2 At(Rect bounds, Point relative) =>
        new((float)(bounds.X + relative.X * bounds.Width), (float)(bounds.Y + relative.Y * bounds.Height));

    private void AddEllipse(Ellipse ellipse, Rect bounds)
    {
        Vector2 center = At(bounds, new Point(.5, .5));
        float radiusX = (float)bounds.Width / 2;
        float radiusY = (float)bounds.Height / 2;
        Color? fill = (ellipse.Fill as SolidColorBrush)?.Color;
        Color? stroke = (ellipse.Stroke as SolidColorBrush)?.Color;
        float thickness = (float)ellipse.StrokeThickness;
        _layers.Add((drawing, _, _) =>
        {
            if (fill is { } fillColor) drawing.FillEllipse(center, radiusX, radiusY, fillColor);
            if (stroke is { } strokeColor && thickness > 0)
                drawing.DrawEllipse(center, radiusX - thickness / 2, radiusY - thickness / 2, strokeColor, thickness);
        });
    }

    private static void DrawCover(CanvasDrawingSession drawing, CanvasBitmap? cover, Rect bounds, float corner)
    {
        if (cover is null) return;
        var source = cover.Bounds;
        double scale = Math.Max(bounds.Width / source.Width, bounds.Height / source.Height);
        double cropWidth = bounds.Width / scale;
        double cropHeight = bounds.Height / scale;
        var crop = new Rect((source.Width - cropWidth) / 2, (source.Height - cropHeight) / 2, cropWidth, cropHeight);
        using var clip = CanvasGeometry.CreateRoundedRectangle(drawing, bounds, corner, corner);
        using var layer = drawing.CreateLayer(1, clip);
        drawing.DrawImage(cover, bounds, crop, 1, CanvasImageInterpolation.HighQualityCubic);
    }

    private sealed class PosterText(TextBlock block, Rect bounds, Color color, double textScale)
    {
        private readonly string _text = block.Text;
        private readonly string _font = block.FontFamily.Source is "XamlAutoFontFamily" or "" ? "Segoe UI" : block.FontFamily.Source;
        private readonly string _locale = CultureInfo.CurrentUICulture.Name;
        private readonly Windows.UI.Text.FontWeight _weight = block.FontWeight;
        private readonly float _size = (float)(block.FontSize * (block.IsTextScaleFactorEnabled ? textScale : 1));
        private readonly int _characterSpacing = block.CharacterSpacing;
        private readonly float _lineHeight = (float)block.LineHeight;
        private readonly bool _wrap = HasWrappedLines(block);
        private readonly float _baseline = (float)block.BaselineOffset;
        private readonly bool _ellipsis = block.TextTrimming is TextTrimming.CharacterEllipsis or TextTrimming.WordEllipsis;

        private static bool HasWrappedLines(TextBlock block)
        {
            if (block.TextWrapping == TextWrapping.NoWrap) return false;
            // DirectWrite's wrap boundary can differ slightly from the XAML renderer. Preserve a
            // line that XAML fitted in full so rounding never hides its last word during export.
            var first = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var last = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            return first.IsEmpty || last.IsEmpty || Math.Abs(last.Y - first.Y) > .5;
        }

        internal void Draw(CanvasDrawingSession drawing)
        {
            using var format = new CanvasTextFormat
            {
                FontFamily = _font,
                FontSize = _size,
                FontWeight = _weight,
                LocaleName = _locale,
                WordWrapping = _wrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap,
                TrimmingGranularity = _ellipsis ? CanvasTextTrimmingGranularity.Character : CanvasTextTrimmingGranularity.None,
                TrimmingSign = _ellipsis ? CanvasTrimmingSign.Ellipsis : CanvasTrimmingSign.None
            };
            if (_lineHeight > 0)
            {
                format.LineSpacing = _lineHeight;
                format.LineSpacingBaseline = _lineHeight * .8f;
            }

            using var layout = new CanvasTextLayout(drawing, _text, format, (float)bounds.Width, (float)bounds.Height);
            if (_characterSpacing != 0) layout.SetCharacterSpacing(0, _text.Length, 0, _size * _characterSpacing / 1000, 0);
            var lines = layout.LineMetrics;
            float y = (float)bounds.Y;
            if (_baseline > 0 && lines.Length != 0)
                y += _baseline - lines[0].Baseline;
            using var clip = drawing.CreateLayer(1, bounds);
            drawing.DrawTextLayout(layout, (float)bounds.X, y, color);
        }
    }
}
