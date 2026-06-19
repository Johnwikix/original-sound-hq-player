using System.Collections.Generic;
using System.Numerics;

namespace AnimatedWin2dControls.Impressionist;

public readonly struct HSVColor
{
    public float H { get; init; }
    public float S { get; init; }
    public float V { get; init; }
}

public sealed class ThemeColorResult
{
    public Vector3 Color { get; }
    public bool ColorIsDark { get; }

    internal ThemeColorResult(Vector3 color, bool colorIsDark)
    {
        Color = color;
        ColorIsDark = colorIsDark;
    }
}

public sealed class PaletteResult
{
    public List<Vector3> Palette { get; }
    public bool PaletteIsDark { get; }
    public ThemeColorResult ThemeColor { get; }

    internal PaletteResult(List<Vector3> palette, bool paletteIsDark, ThemeColorResult themeColor)
    {
        Palette = palette;
        PaletteIsDark = paletteIsDark;
        ThemeColor = themeColor;
    }
}
