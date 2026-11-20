using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

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
    private CanvasDevice _documentDevice;
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
        public bool HasColorGlyphs;

        public void Dispose()
        {
            TextRenderingHelper.DisposeShapedText(Diffs);
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

    private static CanvasTextLayout CreateParagraphLayout(ICanvasResourceCreator sender, double width, string text, CanvasTextFormat format)
    {
        var layout = new CanvasTextLayout(sender, text, format, (float)width, float.MaxValue);
        try
        {
            layout.Options = CanvasDrawTextOptions.EnableColorFont | CanvasDrawTextOptions.NoPixelSnap;
            layout.VerticalAlignment = CanvasVerticalAlignment.Top;
            layout.RequestedSize = new Size(width, Math.Ceiling(layout.LayoutBounds.Height));
            return layout;
        }
        catch
        {
            layout.Dispose();
            throw;
        }
    }

    private static bool HasSameDocumentText(AnimatedTextDocument current, AnimatedTextDocument previous)
    {
        if (ReferenceEquals(current, previous)) return true;
        if (previous is null || current.Paragraphs.Count != previous.Paragraphs.Count) return false;
        for (int i = 0; i < current.Paragraphs.Count; i++)
        {
            if (current.Paragraphs[i].Text != previous.Paragraphs[i].Text) return false;
        }
        return true;
    }

    private void PrepareDocumentLayouts(ICanvasResourceCreator sender, double width, AnimatedTextDocument sourceDocument, bool needsDiff)
    {
        var paragraphs = Document.Paragraphs;
        int oldCount = sourceDocument?.Paragraphs.Count ?? 0;
        var layouts = new ParagraphLayouts[Math.Max(paragraphs.Count, needsDiff ? oldCount : 0)];
        var combinedDiffs = needsDiff && _textEffect is not Effects.TextFadeEffect and not Effects.TextWipeEffect
            ? new List<TextDiffResult>() : null;
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
                item.HasColorGlyphs = ShapedText.MayContainColorGlyphs(newText) || ShapedText.MayContainColorGlyphs(item.OldText);
                layouts[i] = item;
                item.Format = CreateParagraphFormat(paragraph);
                item.NewLayout = CreateParagraphLayout(sender, width, newText, item.Format);
                if (needsDiff)
                {
                    item.OldLayout = CreateParagraphLayout(sender, width, item.OldText, item.Format);
                    // Fade/Wipe operate on complete layouts; grapheme effects share
                    // one combined progress list, including paragraph-wide staggering.
                    if (combinedDiffs is not null)
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
        TextRenderingHelper.DisposeShapedText(_diffResults);
        _documentLayouts = layouts;
        _documentDevice = sender.Device;
        _diffResults = combinedDiffs;
        _renderedDocument = Document;
        _transitionSourceDocument = needsDiff ? sourceDocument : Document;
        _documentFormatVersion = _formatVersion;
        _documentLayoutDirty = false;
    }

    private void DrawDocument(ICanvasResourceCreator sender, double width, CanvasDrawingSession ds)
    {
        bool deviceChanged = !ReferenceEquals(_documentDevice, sender.Device);
        if (deviceChanged || _documentLayoutDirty || _documentLayouts == null || _documentFormatVersion != _formatVersion)
        {
            if (deviceChanged)
                ApplyTextForeground(sender);
            StopHoverScroll();
            bool textChanged = !HasSameDocumentText(Document, _renderedDocument);
            bool start = _textEffect != null && textChanged;
            bool resume = _textEffect != null && !textChanged && _currentState == AnimatedTextBlockRedrawState.Animating;
            if (!start && !resume && _currentState != AnimatedTextBlockRedrawState.Idle)
                SetRedrawState(AnimatedTextBlockRedrawState.Idle, false);

            // The previous text is captured only for a new change. Resizing or switching
            // drawing devices rebuilds the layouts at the current animation time.
            var source = start ? _renderedDocument : resume ? _transitionSourceDocument : Document;
            PrepareDocumentLayouts(sender, width, source, start || resume);
            if (start)
                BeginTextAnimation();
            else if (resume && _diffResults is not null)
                UpdateAllClusterProgress();
        }
        Color foreground = GetDocumentForeground(sender);
        if (_currentState == AnimatedTextBlockRedrawState.Idle)
        {
            EnsureDocumentHoverScroll(sender, (float)width);
            if (_hoverLines != null)
            {
                foreach (var line in _hoverLines)
                    DrawHoverLine(ds, line, foreground);
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
                    DrawTextLayout(ds, item.NewLayout, 0, item.Y, (float)item.Paragraph.Opacity, item.HasColorGlyphs, foreground);
                    continue;
                }
                ds.Transform = Matrix3x2.CreateTranslation(0, item.Y) * originalTransform;
                var color = foreground;
                bool useOpacityLayer = _textBrush != null || item.HasColorGlyphs;
                if (!useOpacityLayer)
                    color.A = (byte)Math.Round(color.A * item.Paragraph.Opacity);
                // Color fonts ignore foreground alpha, so fade the whole paragraph.
                using var opacityLayer = useOpacityLayer && item.Paragraph.Opacity < 1
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

    private Color GetDocumentForeground(ICanvasResourceCreator sender)
    {
        if (!ReferenceEquals(sender, _sceneDevice) || ReadLocalValue(ForegroundProperty) != DependencyProperty.UnsetValue
            || Foreground is not SolidColorBrush themeForeground)
            return _textColor;

        // The theme brush supplies the ink polarity. Keep it opaque in the linear scene,
        // where the Windows SDR brush's alpha would otherwise mix the background into fully covered glyphs.
        Color ink = themeForeground.Color;
        if (ink.A > 0)
            ink.A = 255;
        return ink;
    }

    private void EnsureDocumentHoverScroll(ICanvasResourceCreator sender, float width)
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
                scrollable |= AppendHoverLines(sender, width, paragraph.NewLayout, paragraph.Format,
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
        _documentDevice = null;
    }
}
