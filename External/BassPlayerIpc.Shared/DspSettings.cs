using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>保存 PCM 音效的用户偏好；位流旁路不修改这些偏好。</summary>
public sealed record DspSettings
{
    /// <summary>获取或设置 DSP 总开关；默认开启以保留旧版音效偏好。</summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>获取或设置是否按整曲响度应用固定增益。</summary>
    public bool NormalizeLoudness { get; init; }
    /// <summary>获取或设置目标综合响度，单位 LUFS。</summary>
    public double TargetLufs { get; init; } = -12;
    /// <summary>获取或设置 DSP 前置衰减，单位 dB。</summary>
    public double HeadroomDb { get; init; }
    /// <summary>Null reads legacy separate gain controls; new editors use one manual/automatic preamp.</summary>
    public bool? AutoPreamp { get; init; }

    public DspSettings ToUnifiedGain() => AutoPreamp.HasValue ? Sanitize() : (this with
    {
        AutoPreamp = ConvolutionEnabled && CorrectionCurve.UsesCurve(this) && AutoConvolutionHeadroom,
        HeadroomDb = HeadroomDb + (ConvolutionEnabled ? ConvolutionTrimDb : 0),
        ConvolutionTrimDb = 0, AutoConvolutionHeadroom = false
    }).Sanitize();
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

    public ConvolutionSource ConvolutionSource { get; init; }
    public string CurvePoints { get; init; } = CorrectionCurve.Flat;
    /// <summary>UI-only preset identity, persisted locally and intentionally omitted from audio IPC.</summary>
    public string CurvePresetName { get; init; } = "";
    public bool AutoConvolutionHeadroom { get; init; } = true;
    public bool ConvolutionEnabled { get; init; }
    public string ImpulsePath { get; init; } = "";
    public double ConvolutionTrimDb { get; init; } = -6;

    /// <summary>返回经过有限值和范围校验的设置快照。</summary>
    public DspSettings Sanitize()
    {
        string curve;
        try { curve = CorrectionCurve.Encode(CorrectionCurve.Parse(CurvePoints)); } catch (ArgumentException) { curve = CorrectionCurve.Flat; }
        var source = Enum.IsDefined(ConvolutionSource) ? ConvolutionSource : ConvolutionSource.Automatic;
        double trim = Finite(ConvolutionTrimDb, -24, 0, -6);
        string path = ImpulsePath ?? "";
        if (Encoding.UTF8.GetByteCount(path) > 1024 || path.Contains('\0')) path = "";
        double target = Finite(TargetLufs, -24, -8, -12);
        double headroom = Finite(HeadroomDb, AutoPreamp.HasValue ? -48 : -24, AutoPreamp.HasValue ? 24 : 0, 0);
        double balance = Finite(Balance, -1, 1, 0);
        double crossfeed = Finite(Crossfeed, 0, 0.5, 0);
        double width = Finite(StereoWidth, 0, 1.5, 1);
        if (TargetLufs == target && HeadroomDb == headroom && Balance == balance
            && Crossfeed == crossfeed && StereoWidth == width && ConvolutionTrimDb == trim && ImpulsePath == path && CurvePoints == curve && ConvolutionSource == source) return this;
        return this with { TargetLufs = target, HeadroomDb = headroom, Balance = balance,
            Crossfeed = crossfeed, StereoWidth = width, ConvolutionTrimDb = trim, ImpulsePath = path, CurvePoints = curve, ConvolutionSource = source };
    }

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
    /// <summary>分析失败，保持保守衰减播放。</summary>
    Failed
}

/// <summary>提供播放端当前实际的 PCM/位流及音效状态。</summary>
public readonly record struct DspState(byte RenderKind, bool EqualizerActive, int Channels,
    LoudnessStatus Loudness, double GainDb, double IntegratedLufs, bool IsEnabled = true, ConvolutionStatus Convolution = ConvolutionStatus.Off, int SampleRate = 0, string OutputDeviceId = "");

/// <summary>提供版本化 DSP 协议；独立命令保持旧设置和 EQ 载荷兼容。</summary>
public static class DspProtocol
{
    /// <summary>设置载荷字节数。</summary>
    public const int SettingsSize = 1596;
    /// <summary>状态载荷字节数。</summary>
    public const int StateSize = 288;

    /// <summary>写入 DSP 设置。</summary>
    public static void WriteSettings(Span<byte> data, DspSettings settings)
    {
        data[..SettingsSize].Clear();
        data[0] = 5;
        data[1595] = settings.AutoPreamp switch { true => 2, false => 1, null => 0 };
        data[1] = settings.NormalizeLoudness ? (byte)1 : (byte)0;
        data[2] = settings.SwapChannels ? (byte)1 : (byte)0;
        data[3] = settings.Mono ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(data[4..], settings.TargetLufs);
        BinaryPrimitives.WriteDoubleLittleEndian(data[12..], settings.HeadroomDb);
        BinaryPrimitives.WriteDoubleLittleEndian(data[20..], settings.Balance);
        BinaryPrimitives.WriteDoubleLittleEndian(data[28..], settings.Crossfeed);
        BinaryPrimitives.WriteDoubleLittleEndian(data[36..], settings.StereoWidth);
        data[44] = settings.IsEnabled ? (byte)1 : (byte)0;
        data[45] = settings.ConvolutionEnabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(data[46..], settings.ConvolutionTrimDb);
        int length = Encoding.UTF8.GetBytes(settings.ImpulsePath, data.Slice(56, 1024));
        BinaryPrimitives.WriteUInt16LittleEndian(data[54..], (ushort)length);
        data[1080] = (byte)settings.ConvolutionSource;
        data[1081] = settings.AutoConvolutionHeadroom ? (byte)1 : (byte)0;
        var points = CorrectionCurve.Parse(settings.CurvePoints);
        data[1082] = (byte)points.Length;
        for (int i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(data[(1083 + i * 16)..], points[i].Frequency);
            BinaryPrimitives.WriteDoubleLittleEndian(data[(1091 + i * 16)..], points[i].GainDb);
        }
    }

    /// <summary>读取并校验 DSP 设置。</summary>
    public static DspSettings ReadSettings(ReadOnlySpan<byte> data)
    {
        bool legacy = data.Length == 44 && data[0] == 1;
        bool v2 = data.Length == 45 && data[0] == 2;
        bool v3 = data.Length == 1080 && data[0] == 3;
        bool v5 = data.Length == SettingsSize && data[0] == 5;
        bool v4 = (data.Length == 1595 && data[0] == 4) || v5;
        if (v5 && data[1595] > 2) throw new ArgumentException("Invalid preamp mode.");
        bool convolution = v3 || v4;
        if ((!legacy && ((!v2 && !v3 && !v4) || data[44] > 1))
            || data[1] > 1 || data[2] > 1 || data[3] > 1)
            throw new ArgumentException("Invalid DSP settings payload.");
        if (convolution && (data[45] > 1 || BinaryPrimitives.ReadUInt16LittleEndian(data[54..]) > 1024))
            throw new ArgumentException("Invalid convolution settings.");
        string curve = CorrectionCurve.Flat;
        if (v4)
        {
            if (data[1080] > 2 || data[1081] > 1 || data[1082] is < 2 or > 32) throw new ArgumentException("Invalid curve payload.");
            var points = new CurvePoint[data[1082]];
            for (int i = 0; i < points.Length; i++) points[i] = new(
                BinaryPrimitives.ReadDoubleLittleEndian(data[(1083 + i * 16)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(data[(1091 + i * 16)..]));
            curve = CorrectionCurve.Encode(points);
            CorrectionCurve.Parse(curve);
        }
        return new DspSettings
        {
            AutoPreamp = v5 ? data[1595] switch { 2 => true, 1 => false, _ => (bool?)null } : null,
            ConvolutionSource = v4 ? (ConvolutionSource)data[1080] : ConvolutionSource.Automatic,
            CurvePoints = curve,
            AutoConvolutionHeadroom = !v4 || data[1081] != 0,
            ConvolutionEnabled = convolution && data[45] != 0,
            ConvolutionTrimDb = convolution ? BinaryPrimitives.ReadDoubleLittleEndian(data[46..]) : -6,
            ImpulsePath = convolution ? new UTF8Encoding(false, true).GetString(data.Slice(56, BinaryPrimitives.ReadUInt16LittleEndian(data[54..]))) : "",
            IsEnabled = legacy || data[44] != 0,
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
        data[3] = 5;
        data.Slice(30, 258).Clear();
        int deviceLength = Encoding.UTF8.GetBytes(state.OutputDeviceId ?? "", data.Slice(32, 256));
        BinaryPrimitives.WriteUInt16LittleEndian(data[30..], (ushort)deviceLength);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], state.Channels);
        BinaryPrimitives.WriteDoubleLittleEndian(data[8..], state.GainDb);
        BinaryPrimitives.WriteDoubleLittleEndian(data[16..], state.IntegratedLufs);
        data[24] = state.IsEnabled ? (byte)1 : (byte)0;
        data[25] = (byte)state.Convolution;
        BinaryPrimitives.WriteInt32LittleEndian(data[26..], state.SampleRate);
    }

    /// <summary>读取播放端状态。</summary>
    public static DspState ReadState(ReadOnlySpan<byte> data)
    {
        bool legacy = data.Length == 24 && data[3] == 1;
        bool v2 = data.Length == 25 && data[3] == 2;
        bool v3 = data.Length == 26 && data[3] == 3;
        bool v5 = data.Length == StateSize && data[3] == 5;
        bool v4 = (data.Length == 30 && data[3] == 4) || v5;
        if (v5 && BinaryPrimitives.ReadUInt16LittleEndian(data[30..]) > 256)
            throw new ArgumentException("Invalid output device ID.");
        if ((!legacy && ((!v2 && !v3 && !v4) || data[24] > 1)) || ((v3 || v4) && data[25] > (byte)ConvolutionStatus.Unsupported))
            throw new ArgumentException("Invalid DSP state payload.");
        return new(data[0], data[1] != 0, BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            (LoudnessStatus)data[2], BinaryPrimitives.ReadDoubleLittleEndian(data[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(data[16..]), legacy || data[24] != 0, v3 || v4 ? (ConvolutionStatus)data[25] : ConvolutionStatus.Off,
            v4 ? BinaryPrimitives.ReadInt32LittleEndian(data[26..]) : 0,
            v5 ? new UTF8Encoding(false, true).GetString(data.Slice(32, BinaryPrimitives.ReadUInt16LittleEndian(data[30..]))) : "");
    }
}
