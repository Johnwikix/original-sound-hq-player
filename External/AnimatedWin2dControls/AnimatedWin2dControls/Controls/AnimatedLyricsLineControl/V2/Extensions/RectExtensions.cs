using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2
{
    public static class RectExtensions
    {
        public static Rect AddX(this Rect rect, double x) => new(rect.X + x, rect.Y, rect.Width, rect.Height);
        public static Rect AddY(this Rect rect, double y) => new(rect.X, rect.Y + y, rect.Width, rect.Height);
        public static Rect WithX(this Rect rect, double x) => new(x, rect.Y, rect.Width, rect.Height);
        public static Rect WithY(this Rect rect, double y) => new(rect.X, y, rect.Width, rect.Height);

        public static Rect Extend(this Rect rect, double left, double top, double right, double bottom)
            => new(rect.X - left, rect.Y - top, rect.Width + left + right, rect.Height + top + bottom);

        public static Rect Extend(this Rect rect, double padding)
            => new(rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2);

        public static Rect Extend(this Rect rect, double horizontalPadding, double verticalPadding)
            => Extend(rect, horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);

        public static Rect Scale(this Rect rect, double scale)
        {
            double scaledWidth = rect.Width * scale;
            double scaledHeight = rect.Height * scale;
            double scaleOffsetX = (scaledWidth - rect.Width) / 2;
            double scaleOffsetY = (scaledHeight - rect.Height) / 2;
            return new Rect(rect.X - scaleOffsetX, rect.Y - scaleOffsetY, scaledWidth, scaledHeight);
        }

        public static Vector2 Center(this Rect rect) => new((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
    }
}
