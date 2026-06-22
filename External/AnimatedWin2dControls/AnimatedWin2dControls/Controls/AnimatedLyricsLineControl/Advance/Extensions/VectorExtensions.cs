using System.Numerics;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance
{
    public static class VectorExtensions
    {
        public static Vector2 WithX(this Vector2 v, float x) => new(x, v.Y);
        public static Vector2 WithY(this Vector2 v, float y) => new(v.X, y);
        public static Vector2 AddX(this Vector2 v, float x) => new(v.X + x, v.Y);
        public static Vector2 AddY(this Vector2 v, float y) => new(v.X, v.Y + y);
    }
}
