using System;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.AudioAnalysis;

/// <summary>原采样率声谱图；像素按时间列、频率行排列，强度以 -160…0 dBFS 编码，发布后所有数组只读。</summary>
public sealed record SpectrogramData(int SampleRate, int Channels, int FftSize, int Width, double Duration,
    byte[] Combined, byte[] Left, byte[] Right)
{
    public const int MaxWidth = 1024;
    public int Height => FftSize / 2 + 1;
    public int ByteCount => Combined.Length + Left.Length + Right.Length;
    public byte[] Values(int channel) => channel == 1 && Left.Length > 0 ? Left : channel == 2 && Right.Length > 0 ? Right : Combined;
    public static bool ValidFftSize(int size) => size is 2048 or 4096 or 8192;
}

public sealed record SpectrogramRequest(string Path, int FftSize);

[JsonSerializable(typeof(SpectrogramRequest))]
public partial class SpectrogramJsonContext : JsonSerializerContext;
