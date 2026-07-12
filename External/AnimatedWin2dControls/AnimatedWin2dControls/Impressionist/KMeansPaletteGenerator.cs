using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AnimatedWin2dControls.Impressionist;

internal static class KMeansPaletteGenerator
{
    private static readonly Random s_rng = new();

    public static ThemeColorResult CreateThemeColor(
        Dictionary<Vector3, int> sourceColor,
        bool ignoreWhite = false,
        bool toLab = false)
    {
        var filtered = FilterColors(sourceColor, ignoreWhite, isDarkFilter: null, toLab);
        var centers = KMeansCluster(filtered, 1, useKMeansPP: false, deterministicInit: true);
        var color = centers[0];
        if (toLab) color = color.LABVectorToRGBVector();
        bool isDark = color.RGBVectorLStarIsDark();
        return new ThemeColorResult(color, isDark);
    }

    public static PaletteResult CreatePalette(
        Dictionary<Vector3, int> sourceColor,
        int clusterCount,
        bool ignoreWhite = false,
        bool toLab = false,
        bool useKMeansPP = false,
        bool deterministicInit = true)
    {
        if (sourceColor.Count == 1)
        {
            ignoreWhite = false;
            useKMeansPP = false;
            deterministicInit = false;
        }

        var themeResult = CreateThemeColor(sourceColor, ignoreWhite, toLab);
        bool colorIsDark = themeResult.ColorIsDark;

        bool? darkFilter = colorIsDark ? true : false;
        var filtered = FilterColors(sourceColor, ignoreWhite, darkFilter, toLab);

        if (filtered.Count == 0)
            filtered = FilterColors(sourceColor, false, null, toLab);

        var centers = KMeansCluster(filtered, clusterCount, useKMeansPP, deterministicInit);

        var palette = new List<Vector3>(clusterCount);
        int count = centers.Length;
        for (int i = 0; i < clusterCount; i++)
        {
            var c = centers[i % count];
            if (toLab) c = c.LABVectorToRGBVector();
            palette.Add(c);
        }

        return new PaletteResult(palette, colorIsDark, themeResult);
    }

    private static Dictionary<Vector3, int> FilterColors(
        Dictionary<Vector3, int> source,
        bool ignoreWhite,
        bool? isDarkFilter,
        bool toLab)
    {
        var result = new Dictionary<Vector3, int>(source.Count);
        foreach (var (color, pop) in source)
        {
            if (ignoreWhite && source.Count > 1 &&
                color.X > 250f && color.Y > 250f && color.Z > 250f)
                continue;

            if (isDarkFilter == true && !color.PaletteRGBVectorLStarIsDark())
                continue;
            if (isDarkFilter == false && !color.PaletteRGBVectorLStarIsLight())
                continue;

            var key = toLab ? color.RGBVectorToLABVector() : color;
            ref int existing = ref CollectionsMarshal.GetValueRefOrAddDefault(result, key, out bool exists);
            existing += pop;
        }
        return result;
    }

    private static Vector3[] KMeansCluster(
        Dictionary<Vector3, int> colors,
        int numClusters,
        bool useKMeansPP,
        bool deterministicInit)
    {
        int clusterCount = Math.Min(numClusters, colors.Count);
        if (clusterCount == 0) return [Vector3.Zero];

        var keys = new Vector3[colors.Count];
        colors.Keys.CopyTo(keys, 0);

        Vector3[] centers;
        if (deterministicInit)
        {
            centers = DeterministicInit(colors, keys, clusterCount);
        }
        else if (useKMeansPP)
        {
            centers = KMeansPlusPlusInit(colors, keys, clusterCount);
        }
        else
        {
            FisherYatesShuffle(keys);
            centers = new Vector3[clusterCount];
            Array.Copy(keys, centers, clusterCount);
        }

        var clusterAssignments = ArrayPool<int>.Shared.Rent(keys.Length);
        try
        {
            bool changed = true;
            int iterations = 0;
            while (changed && iterations < 250)
            {
                changed = false;
                iterations++;

                for (int k = 0; k < keys.Length; k++)
                {
                    int nearest = FindNearestCenter(keys[k], centers, clusterCount);
                    if (clusterAssignments[k] != nearest || iterations == 1)
                    {
                        clusterAssignments[k] = nearest;
                        changed = true;
                    }
                }

                Span<float> sumX = stackalloc float[clusterCount];
                Span<float> sumY = stackalloc float[clusterCount];
                Span<float> sumZ = stackalloc float[clusterCount];
                Span<float> counts = stackalloc float[clusterCount];
                sumX.Clear(); sumY.Clear(); sumZ.Clear(); counts.Clear();

                for (int k = 0; k < keys.Length; k++)
                {
                    int ci = clusterAssignments[k];
                    int pop = colors[keys[k]];
                    sumX[ci] += keys[k].X * pop;
                    sumY[ci] += keys[k].Y * pop;
                    sumZ[ci] += keys[k].Z * pop;
                    counts[ci] += pop;
                }

                for (int i = 0; i < clusterCount; i++)
                {
                    if (counts[i] == 0f) continue;
                    var newCenter = new Vector3(sumX[i] / counts[i], sumY[i] / counts[i], sumZ[i] / counts[i]);
                    if (newCenter != centers[i])
                    {
                        centers[i] = newCenter;
                        changed = true;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(clusterAssignments);
        }

        return centers;
    }

    private static int FindNearestCenter(Vector3 color, Vector3[] centers, int count)
    {
        int nearest = 0;
        float minDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            float dist = Vector3.DistanceSquared(color, centers[i]);
            if (dist < minDist) { minDist = dist; nearest = i; }
        }
        return nearest;
    }

    // Gonzalez 远-近确定性初始化：首个中心取 population 最大者作为主色锚点，
    // 后续每轮取与现有中心 min-distance 最大的点。给定相同输入完全可复现。
    private static Vector3[] DeterministicInit(
        Dictionary<Vector3, int> colors,
        Vector3[] keys,
        int clusterCount)
    {
        var centers = new Vector3[clusterCount];
        int n = keys.Length;

        int firstIdx = 0;
        int bestPop = -1;
        for (int k = 0; k < n; k++)
        {
            int pop = colors[keys[k]];
            if (pop > bestPop) { bestPop = pop; firstIdx = k; }
        }
        centers[0] = keys[firstIdx];

        float[] rented = ArrayPool<float>.Shared.Rent(n);
        try
        {
            for (int k = 0; k < n; k++)
                rented[k] = Vector3.DistanceSquared(keys[k], centers[0]);

            for (int i = 1; i < clusterCount; i++)
            {
                int bestK = 0;
                float bestD = -1f;
                for (int k = 0; k < n; k++)
                {
                    if (rented[k] > bestD) { bestD = rented[k]; bestK = k; }
                }
                centers[i] = keys[bestK];

                for (int k = 0; k < n; k++)
                {
                    float d = Vector3.DistanceSquared(keys[k], centers[i]);
                    if (d < rented[k]) rented[k] = d;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented, clearArray: false);
        }

        return centers;
    }

    private static Vector3[] KMeansPlusPlusInit(
        Dictionary<Vector3, int> colors,
        Vector3[] keys,
        int clusterCount)
    {
        var centers = new Vector3[clusterCount];
        int n = keys.Length;

        centers[0] = keys[s_rng.Next(n)];

        float[]? accRented = null;
        Span<float> accDistances = n <= 4096
            ? stackalloc float[n]
            : (accRented = ArrayPool<float>.Shared.Rent(n)).AsSpan(0, n);

        try
        {
            for (int i = 1; i < clusterCount; i++)
            {
                float accumulated = 0f;
                for (int k = 0; k < n; k++)
                {
                    float minDist = Vector3.DistanceSquared(keys[k], centers[0]);
                    for (int c = 1; c < i; c++)
                    {
                        float d = Vector3.DistanceSquared(keys[k], centers[c]);
                        if (d < minDist) minDist = d;
                    }
                    accumulated += minDist;
                    accDistances[k] = accumulated;
                }

                float target = (float)(s_rng.NextDouble() * accumulated);
                for (int k = 0; k < n; k++)
                {
                    if (accDistances[k] >= target)
                    {
                        centers[i] = keys[k];
                        break;
                    }
                }
            }
        }
        finally
        {
            if (accRented is not null)
                ArrayPool<float>.Shared.Return(accRented);
        }

        return centers;
    }

    private static void FisherYatesShuffle(Vector3[] array)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = s_rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
