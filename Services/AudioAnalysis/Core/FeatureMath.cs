using System;
using System.Numerics;
namespace WinUIMusicPlayer.AudioAnalysis;
/// <summary>调用方拥有缓冲；固定维度校准、聚合和相似度不创建临时数组。</summary>
public static class FeatureMath
{
    /// <summary>应用曲库尺度并归一化三个片段及平均向量。</summary>
    public static void Calibrate(ReadOnlySpan<float> raw, ReadOnlySpan<double> mean, ReadOnlySpan<double> scale,
        Span<float> clips, Span<float> output)
    {
        output.Clear();
        for (int c = 0; c < 3; c++)
        {
            var clip = clips.Slice(c * 128, 128);
            for (int i = 0; i < 128; i++) clip[i] = scale[i] < 1e-8 ? 0 : (float)((raw[c * 128 + i] - mean[i]) / scale[i]);
            Normalize(clip);
            for (int i = 0; i < 128; i++) output[i] += clip[i] / 3;
        }
        Normalize(output);
    }
    /// <summary>原地L2归一化，静音保持零向量。</summary>
    public static void Normalize(Span<float> values)
    {
        double norm = 0;
        foreach (float value in values) norm += (double)value * value;
        float factor = (float)(1 / Math.Max(Math.Sqrt(norm), 1e-12));
        int i = 0;
        if (Vector.IsHardwareAccelerated)
            for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
                (new Vector<float>(values[i..]) * factor).CopyTo(values[i..]);
        for (; i < values.Length; i++) values[i] *= factor;
    }
    /// <summary>以硬件SIMD计算归一化向量余弦；无SIMD时使用标量路径。</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
                sum += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        float result = Vector.Sum(sum);
        for (; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }
}
