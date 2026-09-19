using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock;

public sealed partial class AnimatedTextBlock
{
    /// <summary>When set, atomically replaces Text with formatted paragraphs on the same canvas and timeline.</summary>
    public AnimatedTextDocument Document
    {
        get => (AnimatedTextDocument)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(AnimatedTextDocument), typeof(AnimatedTextBlock),
        new PropertyMetadata(null, OnDocumentChanged));

    private AnimatedTextDocument _renderedDocument;
    private AnimatedTextDocument _transitionSourceDocument;
    private ParagraphLayouts[] _documentLayouts;
    private bool _documentLayoutDirty = true;
    private int _documentFormatVersion;

    private sealed class ParagraphLayouts : IDisposable
    {
        public AnimatedTextParagraph Paragraph;
        public string OldText;
        public string NewText;
        public CanvasTextFormat Format;
        public CanvasTextLayout OldLayout;
        public CanvasTextLayout NewLayout;
        public List<TextDiffResult> Diffs;
        public float Y;

        public void Dispose()
        {
            OldLayout?.Dispose();
            NewLayout?.Dispose();
            Format?.Dispose();
        }
    }

    private static void OnDocumentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AnimatedTextBlock)sender;
        control._documentLayoutDirty = true;
        control.InvalidateMeasure();
        // _renderedDocument changes only at Draw: several bindings updating in one
        // dispatcher turn must not replace the old frame with an unrendered snapshot.
        control.SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
        if (args.NewValue == null)
        {
            control.DisposeDocumentLayouts();
            control._renderedDocument = null;
            control._transitionSourceDocument = null;
            control._staticLayoutDirty = true;
        }
    }

    private CanvasTextFormat CreateParagraphFormat(AnimatedTextParagraph paragraph)
    {
        var format = new CanvasTextFormat
        {
            FontFamily = _fontFamily,
            FontSize = (float)paragraph.FontSize,
            FontWeight = paragraph.FontWeight,
            FontStretch = _fontStretch,
            FontStyle = _fontStyle,
            Direction = Win2dHelpers.MapTextDirection(_textDirection),
            HorizontalAlignment = Win2dHelpers.MapCanvasHorizontalAlignment(_textAlignment),
            VerticalAlignment = CanvasVerticalAlignment.Center,
            TrimmingGranularity = Win2dHelpers.MapTrimmingGranularity(_textTrimming),
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            WordWrapping = Win2dHelpers.MapWordWrapping(_textWrapping),
            Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap
        };
        try
        {
            ApplyLineSpacing(format, paragraph.LineHeight);
            return format;
        }
        catch
        {
            format.Dispose();
            throw;
        }
    }

    private Size MeasureDocument(float width)
    {
        double height = 0;
        double measuredWidth = 0;
        foreach (var paragraph in Document.Paragraphs)
        {
            using var format = CreateParagraphFormat(paragraph);
            using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), paragraph.Text, format, width, float.MaxValue);
            layout.TrimmingGranularity = CanvasTextTrimmingGranularity.None;
            layout.TrimmingSign = CanvasTrimmingSign.None;
            layout.HorizontalAlignment = CanvasHorizontalAlignment.Left;
            layout.VerticalAlignment = CanvasVerticalAlignment.Top;
            var bounds = layout.LayoutBounds;
            height += Math.Ceiling(bounds.Height);
            measuredWidth = Math.Max(measuredWidth, Math.Ceiling(bounds.Width));
        }
        return new Size(measuredWidth, height);
    }

    private static CanvasTextLayout CreateParagraphLayout(CanvasControl sender, string text, CanvasTextFormat format)
    {
        var layout = new CanvasTextLayout(sender, text, format, (float)sender.Size.Width, float.MaxValue);
        try
        {
            layout.VerticalAlignment = CanvasVerticalAlignment.Top;
            layout.RequestedSize = new Size(sender.Size.Width, Math.Ceiling(layout.LayoutBounds.Height));
            return layout;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    private void PrepareDocumentLayouts(CanvasControl sender)
    {
        bool needsDiff = _textEffect != null && _currentState == AnimatedTextBlockRedrawState.TextChanged;
        // Layout changes during a transition must keep its original source frame.
        // A new document starts from the last submitted frame instead.
        var sourceDocument = ReferenceEquals(Document, _renderedDocument)
            ? _transitionSourceDocument : _renderedDocument;
        var paragraphs = Document.Paragraphs;
        int oldCount = sourceDocument?.Paragraphs.Count ?? 0;
        var layouts = new ParagraphLayouts[Math.Max(paragraphs.Count, needsDiff ? oldCount : 0)];
        var combinedDiffs = needsDiff ? new List<TextDiffResult>() : null;
        float y = 0;
        try
        {
            for (int i = 0; i < layouts.Length; i++)
            {
                var paragraph = i < paragraphs.Count ? paragraphs[i] : sourceDocument.Paragraphs[i];
                string newText = i < paragraphs.Count ? paragraph.Text : string.Empty;
                var item = new ParagraphLayouts
                {
                    Paragraph = paragraph,
                    OldText = i < oldCount ? sourceDocument.Paragraphs[i].Text : string.Empty,
                    NewText = newText,
                    Y = y
                };
                layouts[i] = item;
                item.Format = CreateParagraphFormat(paragraph);
                item.NewLayout = CreateParagraphLayout(sender, newText, item.Format);
                if (needsDiff)
                {
                    item.OldLayout = CreateParagraphLayout(sender, item.OldText, item.Format);
                    // Fade/Wipe operate on complete layouts; grapheme effects share
                    // one combined progress list, including paragraph-wide staggering.
                    if (_textEffect is not Effects.TextFadeEffect && _textEffect is not Effects.TextWipeEffect)
                    {
                        item.Diffs = GraphemeClusterDiff.Diff(
                            TextRenderingHelper.GenerateGraphemeClusters(item.OldText, item.OldLayout),
                            TextRenderingHelper.GenerateGraphemeClusters(newText, item.NewLayout));
                        combinedDiffs.AddRange(item.Diffs);
                    }
                }
                y += (float)item.NewLayout.RequestedSize.Height;
            }
        }
        catch
        {
            foreach (var item in layouts) item?.Dispose();
            throw;
        }
        DisposeDocumentLayouts();
        _documentLayouts = layouts;
        _diffResults = combinedDiffs;
        _renderedDocument = Document;
        _transitionSourceDocument = needsDiff ? sourceDocument : Document;
        _documentFormatVersion = _formatVersion;
        _documentLayoutDirty = false;
    }

    private void DrawDocument(CanvasControl sender, CanvasDrawingSession ds)
    {
        if (_documentLayoutDirty || _documentLayouts == null || _documentFormatVersion != _formatVersion)
        {
            StopHoverScroll();
            if (_currentState == AnimatedTextBlockRedrawState.Animating)
                SetRedrawState(AnimatedTextBlockRedrawState.TextChanged, false);
            PrepareDocumentLayouts(sender);
        }
        if (_currentState == AnimatedTextBlockRedrawState.TextChanged)
        {
            if (_textEffect == null)
                SetRedrawState(AnimatedTextBlockRedrawState.Idle, false);
            else
                BeginTextAnimation();
        }
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
        {
            EnsureDocumentHoverScroll(sender);
            if (_hoverLines != null)
            {
                foreach (var line in _hoverLines)
                    DrawTextLayout(ds, line.Layout, GetHoverOffset(line.Distance), line.Y, line.Opacity);
                return;
            }
        }

        var originalTransform = ds.Transform;
        try
        {
            foreach (var item in _documentLayouts)
            {
                if (_currentState == AnimatedTextBlockRedrawState.Idle || _textEffect == null)
                {
                    DrawTextLayout(ds, item.NewLayout, 0, item.Y, (float)item.Paragraph.Opacity);
                    continue;
                }
                ds.Transform = Matrix3x2.CreateTranslation(0, item.Y) * originalTransform;
                var color = _textColor;
                color.A = (byte)Math.Round(color.A * item.Paragraph.Opacity);
                // Solid foreground opacity needs no extra offscreen layer.
                using var opacityLayer = _textBrush != null && item.Paragraph.Opacity < 1
                    ? ds.CreateLayer((float)item.Paragraph.Opacity) : null;
                _textEffect.DrawText(item.OldText, item.NewText, item.Diffs,
                    item.OldLayout, item.NewLayout, item.Format, color, _textBrush, _currentState, ds);
                ds.Transform = originalTransform;
            }
        }
        finally
        {
            ds.Transform = originalTransform;
        }
    }

    private void EnsureDocumentHoverScroll(CanvasControl sender)
    {
        if (!CanPrepareHoverScroll()) return;
        _hoverChecked = true;
        bool trimmed = false;
        foreach (var paragraph in _documentLayouts)
            trimmed |= HasTrimmedLine(paragraph.NewLayout);
        if (!trimmed) return;
        var lines = new List<HoverLine>();
        bool scrollable = false;
        try
        {
            foreach (var paragraph in _documentLayouts)
                scrollable |= AppendHoverLines(sender, paragraph.NewLayout, paragraph.Format,
                    paragraph.NewText, paragraph.Y, (float)paragraph.Paragraph.Opacity, lines);
            if (scrollable)
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

    private void DisposeDocumentLayouts()
    {
        if (_documentLayouts == null) return;
        foreach (var paragraph in _documentLayouts) paragraph.Dispose();
        _documentLayouts = null;
    }
}
