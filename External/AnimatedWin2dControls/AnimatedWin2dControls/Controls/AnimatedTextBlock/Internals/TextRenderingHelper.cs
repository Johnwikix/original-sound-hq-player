using Microsoft.Graphics.Canvas.Text;
using System.Collections.Generic;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedTextBlock.Internals;

internal static partial class TextRenderingHelper
{
    public static List<GraphemeCluster> GenerateGraphemeClusters(string source, CanvasTextLayout textLayout)
    {
        return new ShapedText(source, textLayout).Clusters;
    }

    public static void DisposeShapedText(List<TextDiffResult> diffs)
    {
        if (diffs == null) return;
        foreach (var diff in diffs)
        {
            diff.OldGlyphCluster?.ShapedRun?.Owner.Dispose();
            diff.NewGlyphCluster?.ShapedRun?.Owner.Dispose();
        }
    }

    public static Rect GetClusterDrawBounds(GraphemeCluster cluster, CanvasTextLayout textLayout)
    {
        switch (textLayout.HorizontalAlignment)
        {
            case CanvasHorizontalAlignment.Justified:
            case CanvasHorizontalAlignment.Left:
                return new Rect(cluster.LayoutBounds.Left,
                    cluster.LayoutBounds.Y + cluster.LayoutBounds.Height * 0.5,
                    cluster.LayoutBounds.Width,
                    cluster.LayoutBounds.Height);
            case CanvasHorizontalAlignment.Right:
                return new Rect(cluster.LayoutBounds.Right,
                    cluster.LayoutBounds.Y + cluster.LayoutBounds.Height * 0.5,
                    cluster.LayoutBounds.Width,
                    cluster.LayoutBounds.Height);
            case CanvasHorizontalAlignment.Center:
                return new Rect(cluster.LayoutBounds.X + cluster.LayoutBounds.Width * 0.5,
                    cluster.LayoutBounds.Y + cluster.LayoutBounds.Height * 0.5,
                    cluster.LayoutBounds.Width,
                    cluster.LayoutBounds.Height);
        }

        return cluster.LayoutBounds;
    }
}
