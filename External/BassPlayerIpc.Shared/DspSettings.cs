using System.Buffers.Binary;

namespace BassPlayerIpc.Shared;

/// <summary>保存 PCM 音效的用户偏好；位流旁路不修改这些偏好。</summary>
public sealed record DspSettings
{
    /// <summary>获取或设置是否按整曲响度应用固定增益。</summary>
    public bool NormalizeLoudness { get; init; }
    /// <summary>获取或设置目标综合响度，单位 LUFS。</summary>
    public double TargetLufs { get; init; } = -18;
    /// <summary>获取或设置 DSP 前置衰减，单位 dB。</summary>
    public double HeadroomDb { get; init; }
    /// <summary>获取或设置左右平衡，-1 为左，1 为右。</summary>
    public double Balance { get; init; }
    /// <summary>获取或设置是否互换左右声道。</summary>
    public bool SwapChannels { get; init; }
    /// <summary>获取或设置是否将左右声道平均混合为单声道。</summary>
    public bool Mono { get; init; }
    /// <summary>获取或设置耳机低频交叉馈送强度，0 表示关闭。</summary>
    public double Crossfeed { get; init; }
    /// <summary>获取或设置立体声宽度，1 为原始宽度。</summary>
    public double StereoWidth { get; init; } = 1;

    /// <summary>返回经过有限值和范围校验的设置快照。</summary>
    public DspSettings Sanitize() => this with
    {
        TargetLufs = Finite(TargetLufs, -24, -12, -18),
        HeadroomDb = Finite(HeadroomDb, -24, 0, 0),
        Balance = Finite(Balance, -1, 1, 0),
        Crossfeed = Finite(Crossfeed, 0, 0.5, 0),
        StereoWidth = Finite(StereoWidth, 0, 1.5, 1)
    };

    private static double Finite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>标记当前曲目的响度分析状态。</summary>
public enum LoudnessStatus : byte
{
    /// <summary>未启用。</summary>
    Off,
    /// <summary>正在后台分析。</summary>
    Analyzing,
    /// <summary>已应用固定增益。</summary>
    Applied,
    /// <summary>为保留动态和峰值余量，无法达到目标响度。</summary>
    PeakLimited,
    /// <summary>格式、声道布局或内容不支持分析。</summary>
    Unavailable,
    /// <summary>分析失败，继续原始增益播放。</summary>
    Failed
}

/// <summary>提供播放端当前实际的 PCM/位流及音效状态。</summary>
public readonly record struct DspState(byte RenderKind, bool EqualizerActive, int Channels,
    LoudnessStatus Loudness, double GainDb, double IntegratedLufs);

/// <summary>提供版本化 DSP 协议；独立命令保持旧设置和 EQ 载荷兼容。</summary>
public static class DspProtocol
{
    /// <summary>设置载荷字节数。</summary>
    public const int SettingsSize = 44;
    /// <summary>状态载荷字节数。</summary>
    public const int StateSize = 24;

    /// <summary>写入 DSP 设置。</summary>
    public static void WriteSettings(Span<byte> data, DspSettings settings)
    {
        data[0] = 1;
        data[1] = settings.NormalizeLoudness ? (byte)1 : (byte)0;
        data[2] = settings.SwapChannels ? (byte)1 : (byte)0;
        data[3] = settings.Mono ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(data[4..], settings.TargetLufs);
        BinaryPrimitives.WriteDoubleLittleEndian(data[12..], settings.HeadroomDb);
        BinaryPrimitives.WriteDoubleLittleEndian(data[20..], settings.Balance);
        BinaryPrimitives.WriteDoubleLittleEndian(data[28..], settings.Crossfeed);
        BinaryPrimitives.WriteDoubleLittleEndian(data[36..], settings.StereoWidth);
    }

    /// <summary>读取并校验 DSP 设置。</summary>
    public static DspSettings ReadSettings(ReadOnlySpan<byte> data)
    {
        if (data.Length != SettingsSize || data[0] != 1 || data[1] > 1 || data[2] > 1 || data[3] > 1)
            throw new ArgumentException("Invalid DSP settings payload.");
        return new DspSettings
        {
            NormalizeLoudness = data[1] != 0, SwapChannels = data[2] != 0, Mono = data[3] != 0,
            TargetLufs = BinaryPrimitives.ReadDoubleLittleEndian(data[4..]),
            HeadroomDb = BinaryPrimitives.ReadDoubleLittleEndian(data[12..]),
            Balance = BinaryPrimitives.ReadDoubleLittleEndian(data[20..]),
            Crossfeed = BinaryPrimitives.ReadDoubleLittleEndian(data[28..]),
            StereoWidth = BinaryPrimitives.ReadDoubleLittleEndian(data[36..])
        }.Sanitize();
    }

    /// <summary>写入播放端状态。</summary>
    public static void WriteState(Span<byte> data, DspState state)
    {
        data[0] = state.RenderKind;
        data[1] = state.EqualizerActive ? (byte)1 : (byte)0;
        data[2] = (byte)state.Loudness;
        data[3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], state.Channels);
        BinaryPrimitives.WriteDoubleLittleEndian(data[8..], state.GainDb);
        BinaryPrimitives.WriteDoubleLittleEndian(data[16..], state.IntegratedLufs);
    }

    /// <summary>读取播放端状态。</summary>
    public static DspState ReadState(ReadOnlySpan<byte> data)
    {
        if (data.Length != StateSize || data[3] != 1) throw new ArgumentException("Invalid DSP state payload.");
        return new(data[0], data[1] != 0, BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            (LoudnessStatus)data[2], BinaryPrimitives.ReadDoubleLittleEndian(data[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[16..]));
    }
}
