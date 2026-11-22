using System;
using System.Numerics;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;

internal static class LyricsViewportTransform
{
    private const float RadiansPerDegree = MathF.PI / 180;

    public static Matrix4x4 CreatePerspective(Vector2 pivot, Vector3 angles, float depth)
    {
        if (angles == Vector3.Zero) return Matrix4x4.Identity;

        var center = new Vector3(pivot, 0);
        var perspective = Matrix4x4.Identity;
        perspective.M34 = 1 / Math.Clamp(depth, 100, 1000);
        return Matrix4x4.CreateTranslation(-center)
            * Matrix4x4.CreateRotationX(angles.X * RadiansPerDegree)
            * Matrix4x4.CreateRotationY(angles.Y * RadiansPerDegree)
            * Matrix4x4.CreateRotationZ(angles.Z * RadiansPerDegree)
            * perspective
            * Matrix4x4.CreateTranslation(center);
    }

    public static bool TryInvertPerspective(Matrix4x4 projection, out Matrix4x4 inverse)
    {
        // Invert the homography of the z = 0 lyric plane. The complete 4D
        // inverse would need the unknown depth of the projected pixel.
        var plane = new Matrix4x4(
            projection.M11, projection.M12, 0, projection.M14,
            projection.M21, projection.M22, 0, projection.M24,
            0, 0, 1, 0,
            projection.M41, projection.M42, 0, projection.M44);
        return Matrix4x4.Invert(plane, out inverse);
    }

    public static bool TryProject(Vector2 point, Matrix4x4 transform, out Vector2 projected)
    {
        var homogeneous = Vector4.Transform(new Vector4(point, 0, 1), transform);
        projected = new Vector2(homogeneous.X, homogeneous.Y) / homogeneous.W;
        return homogeneous.W > 0 && float.IsFinite(projected.X) && float.IsFinite(projected.Y);
    }

    public static Matrix3x2 CreateLine(Vector2 center, float scale, float angle, float width, bool opensLeft,
        Vector2 origin)
    {
        var transform = Matrix3x2.CreateScale(scale, center);
        if (angle != 0)
        {
            float side = opensLeft ? 1 : -1;
            float hinge = width / 2 + side * width;
            transform *= Matrix3x2.CreateRotation(angle, new Vector2(hinge, center.Y));
            origin.X += MathF.Abs(angle) / MathF.PI * width * side;
        }
        return transform * Matrix3x2.CreateTranslation(origin);
    }

    public static double GetFanAngle(int maxAngle, double delta, double space)
        => maxAngle * RadiansPerDegree * Math.Clamp(delta / Math.Max(space, 1), -1, 1);
}

