using System;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V1;

internal struct CurvePoint
{
    public double TimeMs;
    public float PixelX;
    public float VelIn, VelOut;
}

internal struct RowCurve
{
    public TimeSpan Origin;
    public CurvePoint[]? Points;
    public int Count;
}
