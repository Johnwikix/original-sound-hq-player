using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Interop;

public readonly record struct PlaybackHdrColor(float White, float Peak)
{
    public bool IsEnabled => White > 0f;
    internal float TextGlowPeak => Math.Min(White * 1.15f, Peak);

    public static PlaybackHdrColor FromNits(double white, double peak)
        => double.IsFinite(white) && white is > 0 and <= 10000 &&
           double.IsFinite(peak) && peak is > 0 and <= 10000
            ? new((float)(white / 80), (float)(Math.Max(white, peak) / 80))
            : default;

    public Vector3 GetRgb(Color color)
        => IsEnabled
            ? new Vector3(Decode(color.R), Decode(color.G), Decode(color.B)) * White
            : new Vector3(color.R, color.G, color.B) / 255f;

    public Vector4 GetColor(Color color) => new(GetRgb(color), color.A / 255f);

    private static float Decode(byte component)
    {
        float value = component / 255f;
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }
}

public readonly record struct PlaybackCanvasFrame(CanvasDevice Device, Size Size, float Dpi, PlaybackHdrColor Hdr)
{
    public PlaybackCanvasFrame(ICanvasAnimatedControl canvas) : this(canvas.Device, canvas.Size, canvas.Dpi, default) { }

    public int ConvertDipsToPixels(float value, CanvasDpiRounding rounding)
        {
        float pixels = value * Dpi / 96f;
        return (int)(rounding switch
        {
            CanvasDpiRounding.Floor => MathF.Floor(pixels),
            CanvasDpiRounding.Ceiling => MathF.Ceiling(pixels),
            _ => MathF.Round(pixels, MidpointRounding.AwayFromZero)
        });
    }
}
