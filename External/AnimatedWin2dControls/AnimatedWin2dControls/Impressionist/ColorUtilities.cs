using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AnimatedWin2dControls.Impressionist;

internal static class ColorUtilities
{
    public static HSVColor RGBVectorToHSVColor(this Vector3 color)
    {
        float max = Math.Max(Math.Max(color.X, color.Y), color.Z);
        float min = Math.Min(Math.Min(color.X, color.Y), color.Z);

        float v = max * 100f / 255f;

        if (max == min)
            return new HSVColor { H = 0f, S = 0f, V = v };

        float s = (max - min) / max * 100f;
        float h = 0f;

        if (max == color.X)
        {
            h = 60f * (color.Y - color.Z) / (max - min);
            if (h < 0f) h += 360f;
        }
        else if (max == color.Y)
        {
            h = 60f * (2f + (color.Z - color.X) / (max - min));
            if (h < 0f) h += 360f;
        }
        else if (max == color.Z)
        {
            h = 60f * (4f + (color.X - color.Y) / (max - min));
            if (h < 0f) h += 360f;
        }

        return new HSVColor { H = h, S = s, V = v };
    }

    public static Vector3 HSVColorToRGBVector(this HSVColor hsv)
    {
        float hue = hsv.H >= 360f ? 0f : hsv.H;
        int hi = (int)MathF.Floor(hue / 60f) % 6;
        float f = hue / 60f - hi;
        float p = hsv.V / 100f * (1f - hsv.S / 100f) * 255f;
        float q = hsv.V / 100f * (1f - f * hsv.S / 100f) * 255f;
        float t = hsv.V / 100f * (1f - (1f - f) * hsv.S / 100f) * 255f;
        float vv = hsv.V * 255f / 100f;

        return hi switch
        {
            0 => new Vector3(vv, t, p),
            1 => new Vector3(q, vv, p),
            2 => new Vector3(p, vv, t),
            3 => new Vector3(p, q, vv),
            4 => new Vector3(t, p, vv),
            5 => new Vector3(vv, p, q),
            _ => Vector3.Zero,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 RGBVectorToXYZVector(this Vector3 rgb)
    {
        float r = rgb.X / 255f;
        float g = rgb.Y / 255f;
        float b = rgb.Z / 255f;
        return new Vector3(
            r * 0.4124f + g * 0.3576f + b * 0.1805f,
            r * 0.2126f + g * 0.7152f + b * 0.0722f,
            r * 0.0193f + g * 0.1192f + b * 0.9505f);
    }

    public static Vector3 XYZVectorToRGBVector(this Vector3 xyz)
    {
        float r = xyz.X * 3.2406f - xyz.Y * 1.5372f - xyz.Z * 0.4986f;
        float g = -xyz.X * 0.9689f + xyz.Y * 1.8758f + xyz.Z * 0.0415f;
        float b = xyz.X * 0.0557f - xyz.Y * 0.2040f + xyz.Z * 1.0570f;
        return new Vector3(r * 255f, g * 255f, b * 255f);
    }

    private const float D65X = 0.95047f;
    private const float D65Y = 1f;
    private const float D65Z = 1.0883f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fxyz(float t)
        => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;

    public static Vector3 XYZVectorToLABVector(this Vector3 xyz)
    {
        float fy = Fxyz(xyz.Y / D65Y);
        return new Vector3(
            116f * fy - 16f,
            500f * (Fxyz(xyz.X / D65X) - fy),
            200f * (fy - Fxyz(xyz.Z / D65Z)));
    }

    public static Vector3 LABVectorToXYZVector(this Vector3 lab)
    {
        const float delta = 6f / 29f;
        float fy = (lab.X + 16f) / 116f;
        float fx = fy + lab.Y / 500f;
        float fz = fy - lab.Z / 200f;

        return new Vector3(
            fx > delta ? D65X * fx * fx * fx : (fx - 16f / 116f) * 3f * delta * delta * D65X,
            fy > delta ? D65Y * fy * fy * fy : (fy - 16f / 116f) * 3f * delta * delta * D65Y,
            fz > delta ? D65Z * fz * fz * fz : (fz - 16f / 116f) * 3f * delta * delta * D65Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 RGBVectorToLABVector(this Vector3 rgb)
        => rgb.RGBVectorToXYZVector().XYZVectorToLABVector();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 LABVectorToRGBVector(this Vector3 lab)
        => lab.LABVectorToXYZVector().XYZVectorToRGBVector();

    public static bool PaletteRGBVectorLStarIsDark(this Vector3 rgb)
    {
        var limitedColor = rgb / 255f;
        float y = 0.2126f * ChannelToLin(limitedColor.X)
                + 0.7152f * ChannelToLin(limitedColor.Y)
                + 0.0722f * ChannelToLin(limitedColor.Z);
        return YToLStar(y) <= 40f;
    }

    public static bool PaletteRGBVectorLStarIsLight(this Vector3 rgb)
    {
        var limitedColor = rgb / 255f;
        float y = 0.2126f * ChannelToLin(limitedColor.X)
                + 0.7152f * ChannelToLin(limitedColor.Y)
                + 0.0722f * ChannelToLin(limitedColor.Z);
        return YToLStar(y) >= 60f;
    }

    public static bool RGBVectorLStarIsDark(this Vector3 rgb)
    {
        var limitedColor = rgb / 255f;
        float y = 0.2126f * ChannelToLin(limitedColor.X)
                + 0.7152f * ChannelToLin(limitedColor.Y)
                + 0.0722f * ChannelToLin(limitedColor.Z);
        return YToLStar(y) <= 50f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ChannelToLin(float value)
        => value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float YToLStar(float y)
        => y <= 216f / 24389f
            ? y * (24389f / 27f)
            : MathF.Cbrt(y) * 116f - 16f;
}
