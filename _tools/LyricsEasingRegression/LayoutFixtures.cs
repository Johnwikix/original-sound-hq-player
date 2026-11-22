// CPU-only fixtures for the GPU-owned text layouts. Tests execute the shipping
// LyricsLayoutManager geometry; the text fixture records the width passed to Win2D.
using System.Numerics;

namespace Windows.Foundation
{
    public record struct Point(double X, double Y);
}
namespace Windows.UI.Text
{
    public record struct FontWeight(ushort Weight);
}
namespace Microsoft.Graphics.Canvas
{
    public interface ICanvasResourceCreator { }
    public sealed class CanvasResourceFixture : ICanvasResourceCreator { }
}
namespace Microsoft.Graphics.Canvas.Text
{
    public enum CanvasHorizontalAlignment { Left, Center, Right }
    public enum CanvasVerticalAlignment { Top }
    public enum CanvasWordWrapping { WholeWord }
    public class CanvasTextFormat : IDisposable
    {
        public string? FontFamily { get; set; }
        public Windows.UI.Text.FontWeight FontWeight { get; set; }
        public CanvasVerticalAlignment VerticalAlignment { get; set; }
        public CanvasWordWrapping WordWrapping { get; set; }
        public void Dispose() { }
    }
    public class CanvasTextLayout
    {
        public (double Width, double Height) LayoutBounds => (200, 60);
    }
}
namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    using Microsoft.Graphics.Canvas;
    using Microsoft.Graphics.Canvas.Text;

    public class RenderLyricsLine
    {
        public LyricScrollMotion ScrollMotion { get; } = new();
        public ScaleFixture ScaleTransition { get; } = new();
        public Vector2 TopLeftPosition, BottomRightPosition, CenterPosition, PrimaryPosition;
        public double LayerSpacing => 0;
        public LayerFixture PronunciationLayer { get; } = new();
        public LayerFixture TranslationLayer { get; } = new();
        public CanvasTextLayout? PrimaryTextLayout { get; set; } = new();
        public void ApplyDisplayOptions(bool showTranslation, bool showPronunciation) { }
        public void RecreateTextLayout(ICanvasResourceCreator r, int a, int b, string f,
            double w, double h, CanvasHorizontalAlignment alignment, CanvasTextFormat format)
            => RequestedLayoutWidth = w;
        public double RequestedLayoutWidth { get; private set; }
        public void RecreateTextGeometry() => throw new NotSupportedException();
        public void DisposeCaches() { }
        public void RecreateRenderChars(int width, bool preserveFloatTransitions) => throw new NotSupportedException();
    }
    public sealed class LayerFixture
    {
        // These CPU hit-test fixtures contain only a primary text row.
        public Vector2 Position { get; set; }
        public CanvasTextLayout? Layout => null;
        public double Height => 0;
        public double SlotHeight(double spacing) => 0;
        public ScaleFixture Reveal { get; } = new();
    }
    public class ScaleFixture { public double Value { get; set; } = 1; }
    public static class VectorFixture
    {
        public static Vector2 AddX(this Vector2 vector, float x) => new(vector.X + x, vector.Y);
    }
}
