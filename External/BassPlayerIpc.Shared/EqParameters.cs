namespace BassPlayerIpc.Shared;

/// <summary>UI、预设和播放端共用的 Q 值范围。</summary>
public static class EqParameters
{
    public const float DefaultQ = 1.414f;
    public const double MinQ = 0.1;
    public const double MaxQ = 20;

    public static double NormalizeQ(double q) => double.IsFinite(q) && q > 0
        ? Math.Clamp(q, MinQ, MaxQ) : DefaultQ;
}
