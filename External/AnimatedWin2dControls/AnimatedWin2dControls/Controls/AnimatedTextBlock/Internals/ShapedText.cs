using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;

// Capture DirectWrite's actual fallback faces, glyph advances and baselines once
// per layout. Drawing isolated strings would shape them again and change the
// positions when animation hands over to the complete static text layout.
internal sealed partial class ShapedText : ICanvasTextRenderer, IDisposable
{
    private readonly string _text;
    private readonly CanvasTextLayout _layout;
    private CanvasSolidColorBrush _brush;
    public List<GraphemeCluster> Clusters { get; } = new();
    public float Dpi => 96;
    public bool PixelSnappingDisabled => true;
    public Matrix3x2 Transform => Matrix3x2.Identity;

    public ShapedText(string text, CanvasTextLayout layout)
    {
        _text = text;
        _layout = layout;
        layout.DrawToTextRenderer(this, Vector2.Zero);
        Clusters.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
    }

    internal sealed class GlyphRun
    {
        public ShapedText Owner;
        // Borrowed from DirectWrite: keep the projected reference alive, but do
        // not close a shared font face when this layout is released.
        public CanvasFontFace FontFace;
        public CanvasGlyph[] Glyphs;
        public Vector2 Baseline;
        public Matrix3x2 OrientationTransform;
        public float FontSize;
        public bool Sideways;
        public uint BidiLevel;
        public CanvasTextMeasuringMode MeasuringMode;
        public bool UseColorLayout;
    }

    public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs,
        bool isSideways, uint bidiLevel, object brush, CanvasTextMeasuringMode measuringMode,
        string localeName, string textString, int[] clusterMapIndices, uint characterIndex,
        CanvasGlyphOrientation glyphOrientation)
    {
        // 原生回调可能传入 null；无字形时直接跳过，不创建替代数组。
        if (glyphs == null || glyphs.Length == 0) return;
        var orientationTransform = CanvasTextLayout.GetGlyphOrientationTransform(glyphOrientation, isSideways, point);
        if (clusterMapIndices == null || clusterMapIndices.Length == 0)
        {
            AddCluster(0, glyphs.Length, 0, 0, true);
            return;
        }

        // The map may descend for RTL. Calculate glyph extents in glyph order,
        // then visit character groups in their original logical order.
        var glyphEnds = new int[glyphs.Length];
        foreach (int index in clusterMapIndices) glyphEnds[index] = -1;
        int end = glyphs.Length;
        for (int i = glyphs.Length - 1; i >= 0; i--)
        {
            if (glyphEnds[i] != -1) continue;
            glyphEnds[i] = end;
            end = i;
        }
        var advances = new float[glyphs.Length + 1];
        for (int i = 0; i < glyphs.Length; i++) advances[i + 1] = advances[i] + glyphs[i].Advance;
        for (int start = 0; start < clusterMapIndices.Length;)
        {
            int next = start + 1;
            while (next < clusterMapIndices.Length && clusterMapIndices[next] == clusterMapIndices[start]) next++;
            int glyphStart = clusterMapIndices[start];
            AddCluster(glyphStart, glyphEnds[glyphStart], start, next - start, false, advances[glyphStart]);
            start = next;
        }

        void AddCluster(int glyphStart, int glyphEnd, int characterStart, int characterCount, bool trimming, float advance = 0)
        {
            int offset = (int)characterIndex + characterStart;
            var baseline = point + new Vector2((bidiLevel & 1) == 0 ? advance : -advance, 0);
            var slice = new CanvasGlyph[glyphEnd - glyphStart];
            Array.Copy(glyphs, glyphStart, slice, 0, slice.Length);
            Rect bounds;
            if (!trimming && offset + characterCount <= _text.Length)
            {
                _layout.GetCaretPosition(offset, true, out CanvasTextLayoutRegion region);
                bounds = region.LayoutBounds;
            }
            else
            {
                trimming = true;
                float width = 0;
                foreach (var glyph in slice) width += glyph.Advance;
                bounds = new Rect(baseline.X, baseline.Y - fontSize, width, fontSize);
                offset = _text.Length;
            }
            var cluster = new GraphemeCluster
            {
                Characters = trimming ? "\u2026" : _text.Substring(offset, characterCount),
                Offset = offset,
                Length = characterCount,
                IsTrimmed = trimming,
                LayoutBounds = bounds,
                ShapedRun = new GlyphRun
                {
                    Owner = this, FontFace = fontFace, Glyphs = slice, Baseline = baseline, OrientationTransform = orientationTransform,
                    FontSize = fontSize, Sideways = isSideways, BidiLevel = bidiLevel,
                    MeasuringMode = measuringMode
                }
            };
            cluster.DrawBounds = TextRenderingHelper.GetClusterDrawBounds(cluster, _layout);
            // Win2D DrawGlyphRun is monochrome. Symbols and supplementary
            // characters can use color glyphs (including emoji sequences).
            cluster.ShapedRun.UseColorLayout = MayContainColorGlyphs(cluster.Characters);
            Clusters.Add(cluster);
        }
    }

    public static bool MayContainColorGlyphs(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value > 0xffff || rune.Value == 0xfe0f || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol)
                return true;
        return false;
    }

    public static void Draw(CanvasDrawingSession ds, GraphemeCluster cluster, float x, float y, Color color)
    {
        var run = cluster.ShapedRun;
        if (run == null) return;
        var owner = run.Owner;
        var originalTransform = ds.Transform;
        var offset = new Vector2(x - (float)cluster.DrawBounds.X, y - (float)cluster.DrawBounds.Y);
        try
        {
            if (run.UseColorLayout)
            {
                // Retain DirectWrite's color layers and original baseline. Only
                // these uncommon clusters need a clipped full-layout draw.
                ds.Transform = Matrix3x2.CreateTranslation(offset) * originalTransform;
                using var layer = ds.CreateLayer(color.A / 255f, cluster.LayoutBounds);
                color.A = 255;
                ds.DrawTextLayout(owner._layout, Vector2.Zero, color);
                return;
            }
            owner._brush ??= new CanvasSolidColorBrush(ds, color);
            owner._brush.Color = color;
            ds.Transform = run.OrientationTransform * Matrix3x2.CreateTranslation(offset) * originalTransform;
            ds.DrawGlyphRun(run.Baseline, run.FontFace, run.FontSize, run.Glyphs,
                run.Sideways, run.BidiLevel, owner._brush, run.MeasuringMode);
        }
        finally
        {
            ds.Transform = originalTransform;
        }
    }

    // AnimatedTextBlock doesn't expose text decorations or custom inline objects.
    public void DrawInlineObject(Vector2 point, ICanvasTextInlineObject inlineObject, bool isSideways,
        bool isRightToLeft, object brush, CanvasGlyphOrientation glyphOrientation) { }
    public void DrawUnderline(Vector2 point, float width, float thickness, float offset, float runHeight,
        CanvasTextDirection direction, object brush, CanvasTextMeasuringMode mode, string locale, CanvasGlyphOrientation orientation) { }
    public void DrawStrikethrough(Vector2 point, float width, float thickness, float offset,
        CanvasTextDirection direction, object brush, CanvasTextMeasuringMode mode, string locale, CanvasGlyphOrientation orientation) { }

    public void Dispose()
    {
        _brush?.Dispose();
        _brush = null;
    }
}
