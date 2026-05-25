using System;
using System.Collections.Generic;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V1;

internal static class CurveInterpolator
{
    public static float HermiteInterp(float p0, float p1, float m0, float m1,
        double dt, double elapsed)
    {
        if (dt <= 0) return p1;
        float t = Math.Clamp((float)(elapsed / dt), 0f, 1f);
        float t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * p0
             + (t3 - 2 * t2 + t) * (m0 * (float)dt)
             + (-2 * t3 + 3 * t2) * p1
             + (t3 - t2) * (m1 * (float)dt);
    }

    public static float CalcRevealXHermite(ref RowCurve curve,
        TimeSpan effectiveTime, float minX, float maxX)
    {
        if (curve.Count == 0 || curve.Points is null) return minX;
        var pts = curve.Points;
        int ptCount = curve.Count;
        double elapsedMs = (effectiveTime - curve.Origin).TotalMilliseconds;
        if (elapsedMs <= pts[0].TimeMs) return minX;
        if (elapsedMs >= pts[ptCount - 1].TimeMs) return maxX;

        int lo = 0, hi = ptCount - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid + 1].TimeMs <= elapsedMs) lo = mid + 1;
            else hi = mid;
        }
        ref readonly var p0 = ref pts[lo];
        ref readonly var p1 = ref pts[lo + 1];
        float px = HermiteInterp(p0.PixelX, p1.PixelX, p0.VelOut, p1.VelIn,
                                 p1.TimeMs - p0.TimeMs, elapsedMs - p0.TimeMs);
        return minX + Math.Clamp(px, Math.Min(p0.PixelX, p1.PixelX), Math.Max(p0.PixelX, p1.PixelX));
    }

    public static float CalcRevealXFallback(
        IList<LyricWord> words,
        int lineWordBase, int globalFv, int globalLv,
        float minX, TimeSpan effectiveTime,
        WordLayout[] wordLayouts)
    {
        int firstLocal = globalFv - lineWordBase;
        int lastLocal = globalLv - lineWordBase;
        if (firstLocal < 0 || lastLocal >= words.Count) return minX;
        if (effectiveTime <= TimeSpan.FromMilliseconds(words[firstLocal].StartMs)) return minX;

        float totalW = 0f;
        for (int gi = globalFv; gi <= globalLv; gi++)
            totalW += wordLayouts[gi].FullWidth;

        if (effectiveTime >= TimeSpan.FromMilliseconds(words[lastLocal].StartMs + words[lastLocal].DurationMs))
            return minX + totalW;

        float accX = minX;
        for (int gi = globalFv; gi <= globalLv; gi++)
        {
            int local = gi - lineWordBase;
            ref readonly var wl = ref wordLayouts[gi];
            if (wl.FullWidth <= 0) continue;
            var word = words[local];
            var wordEnd = TimeSpan.FromMilliseconds(word.StartMs + word.DurationMs);
            if (effectiveTime >= wordEnd)
            {
                accX += wl.FullWidth;
            }
            else if (effectiveTime >= TimeSpan.FromMilliseconds(word.StartMs))
            {
                float t = word.DurationMs > 0
                    ? Math.Clamp((float)((effectiveTime.TotalMilliseconds - word.StartMs)
                                         / word.DurationMs), 0f, 1f)
                    : 1f;
                float t2 = t * t;
                accX += wl.FullWidth * (t2 * (3f - 2f * t));
                break;
            }
            else break;
        }
        return accX;
    }
}
