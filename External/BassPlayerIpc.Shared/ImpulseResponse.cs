using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>Bounded WAV IR reader shared by the importer and audio process.</summary>
public sealed record ImpulseResponse(int SampleRate, double[][] Channels)
{
    /// <summary>单声道最大抽头数，叠加两秒时长和所有输出通道的总预算。</summary>
    public const int MaxTaps = 262144;
    /// <summary>获取 WAV 声道掩码；零表示只声明声道顺序。</summary>
    public uint ChannelMask { get; init; }
    /// <summary>所有输出声道的最大总抽头数。</summary>
    public const int MaxTotalTaps = 1048576;
    /// <summary>校验时长与内存预算，防止后台准备及切换时无界占用。</summary>
    public static void ValidateBudget(int rate, int channels, int count)
    {
        if (rate is < 8000 or > 768000 || channels is < 1 or > 8 || count < 1
            || count > MaxTaps || count > (long)rate * 2 || (long)count * channels > MaxTotalTaps)
            throw new InvalidDataException("IR exceeds channel, two-second or tap budget.");
    }

    public static ImpulseResponse Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("IR file exceeds 64 MiB.");
        if (stream.Length < 12 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Expected RIFF WAV.");
        long end = reader.ReadUInt32() + 8L;
        if (end > stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid WAV.");
        int format = 0, channels = 0, rate = 0, bits = 0, align = 0;
        uint channelMask = 0;
        long dataOffset = 0;
        uint dataLength = 0;
        while (stream.Position + 8 <= end)
        {
            uint id = reader.ReadUInt32(), size = reader.ReadUInt32();
            long next = stream.Position + size;
            if (next > end) throw new InvalidDataException("Truncated WAV chunk.");
            if (id == 0x20746d66)
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV format.");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22) throw new InvalidDataException("Invalid extensible WAV.");
                    int validBits = reader.ReadUInt16();
                    channelMask = reader.ReadUInt32();
                    var subFormat = new Guid(reader.ReadBytes(16));
                    format = subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1
                        : subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : 0;
                    if (validBits != 0 && validBits != bits) throw new InvalidDataException("Unsupported valid bits.");
                }
            }
            else if (id == 0x61746164 && dataOffset == 0) { dataOffset = stream.Position; dataLength = size; }
            stream.Position = next + (size & 1);
        }
        if (channels is < 1 or > 8 || rate is < 8000 or > 768000 || dataOffset == 0
            || !(format == 1 && bits is 16 or 24 or 32 || format == 3 && bits is 32 or 64)
            || align != channels * (bits / 8) || dataLength == 0 || dataLength % align != 0)
            throw new InvalidDataException("Use 1–8 channel PCM 16/24/32-bit or float WAV.");
        if (channelMask != 0 && System.Numerics.BitOperations.PopCount(channelMask) != channels)
            throw new InvalidDataException("IR speaker mask does not match channels.");
        int count = checked((int)(dataLength / align));
        ValidateBudget(rate, channels, count);
        double[][] samples = new double[channels][];
        for (int ch = 0; ch < channels; ch++) samples[ch] = new double[count];
        stream.Position = dataOffset;
        bool nonzero = false;
        for (int i = 0; i < count; i++)
            for (int ch = 0; ch < channels; ch++)
            {
                double value = format == 3 ? bits == 32 ? reader.ReadSingle() : reader.ReadDouble()
                    : bits == 16 ? reader.ReadInt16() / 32768.0
                    : bits == 24 ? ReadInt24(reader) / 8388608.0 : reader.ReadInt32() / 2147483648.0;
                if (!double.IsFinite(value) || Math.Abs(value) > 64) throw new InvalidDataException("Invalid IR coefficient.");
                samples[ch][i] = value;
                nonzero |= value != 0;
            }
        if (!nonzero) throw new InvalidDataException("Silent IR.");
        return new(rate, samples) { ChannelMask = channelMask };
    }

    public ImpulseResponse AtRate(int rate)
    {
        if (Channels.Length is < 1 or > 8 || Channels[0].Length == 0
            || Channels.Any(c => c.Length != Channels[0].Length || c.Any(x => !double.IsFinite(x))))
            throw new InvalidDataException("Invalid IR coefficients.");
        ValidateBudget(SampleRate, Channels.Length, Channels[0].Length);
        if (rate == SampleRate) return this;
        if (rate is < 8000 or > 768000) throw new ArgumentOutOfRangeException(nameof(rate));
        int count = checked((int)Math.Ceiling(Channels[0].Length * (double)rate / SampleRate));
        ValidateBudget(rate, Channels.Length, count);
        double ratio = (double)SampleRate / rate, cutoff = Math.Min(1, 1 / ratio);
        int radius = (int)Math.Ceiling(32 / cutoff);
        var result = new double[Channels.Length][];
        for (int ch = 0; ch < result.Length; ch++)
        {
            result[ch] = new double[count];
            for (int i = 0; i < count; i++)
            {
                double x = i * ratio, value = 0;
                for (int j = Math.Max(0, (int)x - radius); j <= Math.Min(Channels[ch].Length - 1, (int)x + radius); j++)
                {
                    double d = (x - j) * cutoff;
                    if (Math.Abs(d) >= 32) continue;
                    double sinc = Math.Abs(d) < 1e-12 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                    value += Channels[ch][j] * cutoff * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * d / 32)) * ratio;
                }
                result[ch][i] = value;
            }
        }
        return new(rate, result) { ChannelMask = ChannelMask };
    }

    private static int ReadInt24(BinaryReader reader) => (reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16) << 8 >> 8;
}

public enum ConvolutionStatus : byte { Off, Loading, Active, Failed, Unsupported }
