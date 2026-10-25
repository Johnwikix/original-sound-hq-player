using System.Security.Cryptography;

namespace AudioPlayer.AudioEnhancement.Coefficients;

/// <summary>读取经过长度和摘要校验的紧凑 DSP 系数资源。</summary>
internal sealed class CoefficientStore
{
    internal const uint ResourceFlag = 1u << 31;
    private const int ByteLength = 10528;
    private const string Digest = "a54f49c435f9dad95f5d41b9ac106541fcd7be72c04b83399c3f392896d728d0";
    private readonly byte[] _bytes;

    private CoefficientStore()
    {
        using var stream = typeof(CoefficientStore).Assembly.GetManifestResourceStream("AudioPlayer.AudioEnhancement.Coefficients.dsp-tables.bin")
            ?? throw new InvalidOperationException("Missing embedded DSP coefficient tables.");
        _bytes = new byte[ByteLength];
        stream.ReadExactly(_bytes);
        if (stream.ReadByte() != -1 || !Convert.ToHexString(SHA256.HashData(_bytes)).Equals(Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DSP coefficient tables have an incorrect length or digest.");
    }

    internal static CoefficientStore Default { get; } = new();
    internal static bool IsCoefficient(uint location) => (location & ResourceFlag) != 0;
    internal ReadOnlySpan<byte> Read(uint location, int count)
    {
        if (!IsCoefficient(location)) throw new ArgumentOutOfRangeException(nameof(location));
        return _bytes.AsSpan(checked((int)(location & ~ResourceFlag)), count);
    }
}
