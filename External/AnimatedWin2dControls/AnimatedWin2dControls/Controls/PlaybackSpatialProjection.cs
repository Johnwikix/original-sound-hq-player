using System;
using System.Numerics;

namespace AnimatedWin2dControls.Controls;

/// <summary>Builds a common projection for the playback foreground in device-independent units.</summary>
public static class PlaybackSpatialProjection
{
    public static Matrix4x4 CreatePerspective(Vector2 pivot, Vector3 angles, float depth)
        => CreatePerspective(pivot, angles, depth, Vector2.Zero);

    /// <summary>Builds a projection that keeps the supplied source bounds in front of the camera.</summary>
    public static Matrix4x4 CreatePerspective(Vector2 pivot, Vector3 angles, float depth, Vector2 halfExtents)
    {
        if (angles == Vector3.Zero) return Matrix4x4.Identity;

        const float radiansPerDegree = MathF.PI / 180;
        const float minimumW = 0.5f;
        var center = new Vector3(pivot, 0);
        var rotationX = Matrix4x4.CreateRotationX(angles.X * radiansPerDegree);
        var rotationY = Matrix4x4.CreateRotationY(angles.Y * radiansPerDegree);
        var rotationZ = Matrix4x4.CreateRotationZ(angles.Z * radiansPerDegree);
        var rotation = rotationX * rotationY * rotationZ;
        float nearestDepth = MathF.Abs(rotation.M13) * halfExtents.X + MathF.Abs(rotation.M23) * halfExtents.Y;
        // Here w = 1 + z / depth. Even at the shallowest setting, keep foreground
        // corners away from w = 0, where their projected bounds would diverge.
        float safeDepth = MathF.Max(Math.Clamp(depth, 100, 1000), nearestDepth / (1 - minimumW));
        var perspective = Matrix4x4.Identity;
        perspective.M34 = 1 / safeDepth;
        return Matrix4x4.CreateTranslation(-center) * rotation * perspective * Matrix4x4.CreateTranslation(center);
    }

    public static Matrix4x4 ToLocal(Matrix4x4 projection, Vector2 origin)
    {
        if (projection.IsIdentity || origin == Vector2.Zero) return projection;

        var offset = new Vector3(origin, 0);
        return Matrix4x4.CreateTranslation(offset) * projection * Matrix4x4.CreateTranslation(-offset);
    }
}
