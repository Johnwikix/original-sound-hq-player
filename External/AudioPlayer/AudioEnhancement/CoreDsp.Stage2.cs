using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    // 推进线性同余随机数生成器，并返回一个 15 位数值。
    internal static uint NextRandom15(DspMemory m, uint seed)
    {
        uint next = unchecked(m.U32(seed) * 1103515245u + 12345u);
        m.U32(seed, next);
        return (next >> 16) & 32767u;
    }

    // 生成立体声激励信号，并根据测得的声道能量缩放。
    internal static void GenerateShapedExcitation(DspMemory m, uint s)
    {
        int radius = m.I32(OffsetAddress(s,DspStateLayout.ExcitationHistoryRadius));
        for (int i = 0; i < 32; i++)
        {
            m.F32(OffsetAddress(s,DspStateLayout.LeftExcitationTail + radius * 8 + i * 4), (NextRandom15(m, OffsetAddress(s,DspStateLayout.RandomSeed)) * 2.0 - 32768.0) / 32768.0);
            m.F32(OffsetAddress(s,DspStateLayout.RightExcitationTail + radius * 8 + i * 4), (NextRandom15(m, OffsetAddress(s,DspStateLayout.RandomSeed)) * 2.0 - 32768.0) / 32768.0);
        }
        for (int channel = 0; channel < 2; channel++)
        {
            uint p = OffsetAddress(s,DspStateLayout.LeftExcitationWindow + channel * DspStateLayout.ChannelStride);
            m.Copy(OffsetAddress(p, -0x90), p, radius * 8);
            m.Copy(p, OffsetAddress(p, -0x10), radius * 8);
            float energy = m.F32(OffsetAddress(p, -0x194));
            float db = (float)((float)Math.Log10(energy) * 10.0);
            float gain = energy > m.F64(CoefficientTables.PowerThreshold) ? m.F32(OffsetAddress(s,DspStateLayout.ExcitationGain)) : 0;
            float offset = m.F32(OffsetAddress(s,DspStateLayout.LevelOffset));
            if (db < -110.0 - offset) gain = 0;
            else if (db < -80.0 - offset)
                // 先将 offset 与 80.0 相加，再加上 db，中间不进行 float 舍入。
                gain = (float)((float)Math.Pow(10.0, (db + (offset + 80.0)) / 20.0) * (double)gain);
            for (int i = 0; i < radius * 2 + 32; i++)
            {
                uint q = OffsetAddress(p, -0x90 + i * 4);
                m.F32(q, m.F32(q) * (double)gain);
            }
        }
    }

    // 累加十六个间隔为八的样本功率。
    internal static void MeasureStridedPower(DspMemory m, uint input, uint output)
    {
        float power = 0;
        for (int i = 0; i < 128; i += 8)
        {
            double value = m.F32(OffsetAddress(input, i * 4));
            power = (float)(value * value + power);
        }
        m.F32(output, power);
    }

    // 每隔八个位置采样，测量四个 21 抽头对称分析 FIR 的能量。
    internal static void MeasureRateExtensionBandEnergies(DspMemory m, uint coefficients, uint input, uint output)
    {
        Span<float> powers = stackalloc float[5];
        Span<float> sums = stackalloc float[4];
        powers.Clear();
        for (int i = 0; i < 16; i++)
        {
            int center = 10 + i * 8;
            for (int band = 0; band < 4; band++)
            {
                float value = (float)(m.F32(OffsetAddress(input, center * 4)) * (double)m.F32(OffsetAddress(coefficients, band * 4)));
                for (int radius = 1; radius <= 10; radius++)
                {
                    float pair = (float)(m.F32(OffsetAddress(input, (center - radius) * 4)) + (double)m.F32(OffsetAddress(input, (center + radius) * 4)));
                    value = (float)(pair * (double)m.F32(OffsetAddress(coefficients, (radius * 4 + band) * 4)) + value);
                }
                sums[band] = value;
                powers[band] = (float)(value * (double)value + powers[band]);
            }
            float total = (float)(sums[3] + (double)sums[2]);
            total = (float)(total + (double)sums[1]);
            total = (float)(total + (double)sums[0]);
            powers[4] = (float)(total * (double)total + powers[4]);
        }
        for (int i = 0; i < 5; i++) m.F32(OffsetAddress(output, i * 4), powers[i]);
    }

    // 归一化频带能量，并使用受限增益组合分析系数。
    internal static void BuildRateExtensionAnalysisFilter(DspMemory m, uint coefficients, uint energies, uint output, float level, float limit, uint s)
    {
        for (int i = 0; i < 5; i++)
        {
            uint p = OffsetAddress(energies, i * 4);
            float energy = (float)(m.F32(p) * 0.0625);
            m.F32(p, Math.Clamp(energy, m.F32(CoefficientTables.MinimumEnergy), 1.0f));
        }
        Span<float> gains = stackalloc float[4];
        int reference = m.I32(OffsetAddress(s,DspStateLayout.EnergyReferenceIndex));
        for (int i = 0; i < 4; i++)
        {
            float gain = i < 2 ? (float)((float)Math.Sqrt(m.F32(OffsetAddress(energies, reference * 4)) / (double)m.F32(OffsetAddress(energies, i * 4))) * (double)level) : 0;
            gains[i] = Math.Min(gain, limit);
        }
        for (int i = 0; i < 11; i++)
        {
            float sum = 0;
            for (int band = 0; band < 4; band++)
                sum = (float)(m.F32(OffsetAddress(coefficients, (i * 4 + band) * 4)) * (double)gains[band] + sum);
            m.F32(OffsetAddress(output, i * 4), sum);
        }
    }

    // 按指定输出步长对 21 抽头对称 FIR 的结果抽取。
    internal static void DecimateSymmetricFir(DspMemory m, uint coefficients, int step, uint input, uint output)
    {
        int outIndex = 0;
        for (int center = 10; center < 138; center += step)
        {
            float sum = (float)(m.F32(OffsetAddress(input, center * 4)) * (double)m.F32(coefficients));
            for (int radius = 1; radius <= 10; radius++)
            {
                double pair = m.F32(OffsetAddress(input, (center - radius) * 4)) + (double)m.F32(OffsetAddress(input, (center + radius) * 4));
                sum = (float)(pair * (double)m.F32(OffsetAddress(coefficients, radius * 4)) + sum);
            }
            m.F32(OffsetAddress(output, outIndex++ * 4), sum);
        }
    }

    // 执行具有 2、4、8 或 16 个相位的对称多相插值。
    internal static void InterpolatePolyphase(DspMemory m, uint descriptor, int ratio, uint input, uint output, int add)
    {
        if (TryInterpolateAvx(m, descriptor, ratio, input, output, add)) return;
        if (TryInterpolateSse2(m, descriptor, ratio, input, output, add)) return;
        uint coefficients = m.U32(descriptor);
        int radius = m.I32(OffsetAddress(descriptor, 4));
        int count = ratio < 8 ? 128 : 32;
        Span<float> sums = stackalloc float[4];
        if (ratio != 2 && ratio != 4 && ratio != 8 && ratio != 16) return;
        for (int n = radius; n < radius + count; n++)
        {
            if (ratio <= 4)
            {
                sums.Clear();
                sums[0] = (float)(m.F32(OffsetAddress(input, n * 4)) * (double)m.F32(coefficients));
                for (int r = 1; r <= radius; r++)
                {
                    double left = m.F32(OffsetAddress(input, (n - r) * 4));
                    double right = m.F32(OffsetAddress(input, (n + r) * 4));
                    double leftNext = m.F32(OffsetAddress(input, (n - r + 1) * 4));
                    sums[0] = (float)((left + right) * m.F32(OffsetAddress(coefficients, r * ratio * 4)) + sums[0]);
                    if (ratio == 2)
                        sums[1] = (float)((leftNext + right) * m.F32(OffsetAddress(coefficients, (r * 2 - 1) * 4)) + sums[1]);
                    else
                    {
                        double near = m.F32(OffsetAddress(coefficients, (r * 4 - 3) * 4));
                        double far = m.F32(OffsetAddress(coefficients, (r * 4 - 1) * 4));
                        sums[1] = (float)(near * leftNext + far * right + sums[1]);
                        sums[2] = (float)((leftNext + right) * m.F32(OffsetAddress(coefficients, (r * 4 - 2) * 4)) + sums[2]);
                        sums[3] = (float)(near * right + far * leftNext + sums[3]);
                    }
                }
                for (int phase = 0; phase < ratio; phase++) m.F32(OffsetAddress(output, ((n - radius) * ratio + phase) * 4), sums[phase] * (double)ratio);
                continue;
            }
            for (int phase = 0; phase < ratio; phase++)
            {
                float sum = 0;
                for (int j = 0; j < radius; j++)
                {
                    double left = m.F32(OffsetAddress(input, (n - j) * 4));
                    double right = m.F32(OffsetAddress(input, (n + j + 1) * 4));
                    double leftProduct = left * m.F32(OffsetAddress(coefficients, (j * ratio + phase) * 4));
                    double rightProduct = right * m.F32(OffsetAddress(coefficients, ((j + 1) * ratio - phase) * 4));
                    sum = (float)(leftProduct + sum);
                    sum = (float)(rightProduct + sum);
                }
                if (phase == 0)
                    sum = (float)(m.F32(OffsetAddress(input, (n - radius) * 4)) * (double)m.F32(OffsetAddress(coefficients, radius * ratio * 4)) + sum);
                uint dest = OffsetAddress(output, ((n - radius) * ratio + phase) * 4);
                m.F32(dest, sum * (double)ratio + (ratio >= 8 ? (double)(uint)add * m.F32(dest) : 0.0));
            }
        }
    }

    // 根据对数能量预测升频增益，并更新合成滤波器。
    internal static void UpdateRateExtensionSynthesisFilters(DspMemory m, uint s)
    {
        m.Clear(OffsetAddress(s,DspStateLayout.SynthesisCoefficients), 50 * 4);
        Span<float> gains = stackalloc float[8];
        Span<float> db = stackalloc float[3];
        int reference = m.I32(OffsetAddress(s,DspStateLayout.EnergyReferenceIndex));
        for (int channel = 0; channel < 2; channel++)
        {
            for (int i = 0; i < 3; i++)
            {
                float logarithm = (float)Math.Log10(m.F32(OffsetAddress(s,DspStateLayout.LeftExtensionEnergies + reference * 4 + channel * DspStateLayout.ChannelStride + i * 4)));
                db[i] = (float)((float)(logarithm * 10.0) + 120.0);
            }
            for (int band = 0; band < 4; band++)
            {
                uint c = OffsetAddress(s,DspStateLayout.ExtensionPredictors + band * 16);
                float prediction = m.F32(c);
                for (int i = 0; i < 3; i++) prediction = (float)(db[i] * (double)m.F32(OffsetAddress(c, 4 + i * 4)) + prediction);
                gains[band * 2 + channel] = (float)Math.Pow(10.0, (prediction - (double)db[2]) / 20.0);
            }
        }
        bool bypass = m.I32(OffsetAddress(s,DspStateLayout.SynthesisMode)) == 2;
        for (int channel = 0; channel < 2; channel++)
        {
            uint bases = m.U32(OffsetAddress(s,DspStateLayout.ExtensionSynthesisBasis));
            uint coefficients = OffsetAddress(s,DspStateLayout.SynthesisCoefficients + channel * 100);
            for (int band = 0; band < 4; band++)
            {
                if (bypass && band != 0) continue;
                float gain = bypass ? 1 : gains[band * 2 + channel];
                for (int j = 0; j < 25; j++)
                {
                    uint target = OffsetAddress(coefficients, j * 4);
                    m.F32(target, m.F32(OffsetAddress(bases, (band * 25 + j) * 4)) * (double)gain + m.F32(target));
                }
            }
        }
        uint prototype = m.U32(OffsetAddress(s,DspStateLayout.ExtensionPrototype));
        int length = m.I32(OffsetAddress(s,DspStateLayout.ExtensionPrototypeLength)) + 25;
        if(TrySynthesizePrototype(m,s,prototype,length))return;
        for (int channel = 0; channel < 2; channel++)
        {
            uint output = m.U32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors + channel * 8));
            uint coefficients = OffsetAddress(s,DspStateLayout.SynthesisCoefficients + channel * 100);
            for (int i = 0; i < length; i++)
            {
                int center = 24 + i;
                float sum = (float)(m.F32(OffsetAddress(prototype, center * 4)) * (double)m.F32(coefficients));
                for (int radius = 1; radius <= 24; radius++)
                {
                    float pair = (float)(m.F32(OffsetAddress(prototype, (center - radius) * 4)) + (double)m.F32(OffsetAddress(prototype, (center + radius) * 4)));
                    sum = (float)(pair * (double)m.F32(OffsetAddress(coefficients, radius * 4)) + sum);
                }
                m.F32(OffsetAddress(output, i * 4), sum);
            }
        }
    }

    // 分析、合成并插值升频信号，同时保留声道历史。
    internal static void ProcessRateExtension(DspMemory m, uint s, uint config, int inputFrames, int outputFrames, int offset, uint input, uint output)
    {
        int channels = m.I32(config);
        for (int channel = 0; channel < channels; channel++)
        {
            uint c = OffsetAddress(s,DspStateLayout.LeftExtensionInput + channel * DspStateLayout.ChannelStride);
            uint buffer = m.U32(c);
            int history = m.I32(OffsetAddress(s,DspStateLayout.InputHistoryRadius));
            for (int i = 0; i < inputFrames; i++) m.U32(OffsetAddress(buffer, history * 8 + i * 4), m.U32(OffsetAddress(input, (i * channels + channel) * 4)));
            m.Clear(OffsetAddress(buffer, (inputFrames + history * 2) * 4), (128 - inputFrames) * 4);
            uint shortOutput = OffsetAddress(c, m.I32(OffsetAddress(s,DspStateLayout.AnalysisHistoryRadius)) * 8 + 52);
            if (m.I32(OffsetAddress(s,DspStateLayout.SynthesisMode)) == 2)
            {
                for (int i = 0; i < inputFrames / 4; i++) m.U32(OffsetAddress(shortOutput, i * 4), m.U32(OffsetAddress(buffer, history * 8 + i * 16)));
            }
            else
            {
                if (m.I32(OffsetAddress(s,DspStateLayout.SynthesisMixMode)) == 0)
                    InterpolatePolyphase(m, OffsetAddress(s,DspStateLayout.InputInterpolationFilter), m.I32(OffsetAddress(s,DspStateLayout.InterpolationRatio)), buffer, OffsetAddress(m.U32(OffsetAddress(c, -4)), m.I32(OffsetAddress(s,DspStateLayout.DirectOutputOffset)) * 4), 0);
                ProcessRecursiveFilterCascade(m, OffsetAddress(s,DspStateLayout.ExtensionAnalysisCascade), OffsetAddress(buffer, history * 8), OffsetAddress(buffer, 0x250 + history * 8), OffsetAddress(c, 8));
                MeasureRateExtensionBandEnergies(m, m.U32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisBank)), OffsetAddress(buffer, 0x200 + history * 8), OffsetAddress(c, 16));
                MeasureStridedPower(m, OffsetAddress(buffer, 0x250 + history * 8), OffsetAddress(c, 32));
                BuildRateExtensionAnalysisFilter(m, m.U32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisBank)), OffsetAddress(c, 16), m.U32(OffsetAddress(c, 4)), m.F32(OffsetAddress(s,DspStateLayout.ExtensionGain)), m.F32(OffsetAddress(s,DspStateLayout.ExtensionGainLimit)), s);
                DecimateSymmetricFir(m, m.U32(OffsetAddress(c, 4)), 4, OffsetAddress(buffer, 0x200 + history * 8), shortOutput);
            }
        }
        UpdateRateExtensionSynthesisFilters(m, s);
        GenerateShapedExcitation(m, s);
        for (int channel = 0; channel < channels; channel++)
        {
            uint c = OffsetAddress(s,DspStateLayout.LeftExtensionOutput + channel * DspStateLayout.ChannelStride);
            uint buffer = m.U32(c);
            int mode = m.I32(OffsetAddress(s,DspStateLayout.SynthesisMode));
            uint descriptor = OffsetAddress(s,DspStateLayout.SynthesisDescriptors + channel * 8);
            if (mode != 1)
            {
                uint dest = OffsetAddress(buffer, m.I32(OffsetAddress(s,DspStateLayout.RetainedOutputSamples)) * 4);
                int ratio = m.I32(OffsetAddress(s,DspStateLayout.InterpolationRatio)) * 4;
                InterpolatePolyphase(m, descriptor, ratio, OffsetAddress(c, 56), dest, mode == 2 ? 0 : 1);
                if (mode != 2) InterpolatePolyphase(m, OffsetAddress(descriptor, 16), ratio, OffsetAddress(c, 296), dest, 1);
            }
            WriteInterleavedChannel(m, OffsetAddress(buffer, offset * 4), output, outputFrames, m.I32(OffsetAddress(config, 28)), channel, channels);
            uint source = m.U32(OffsetAddress(c, 4));
            int history = m.I32(OffsetAddress(s,DspStateLayout.InputHistoryRadius));
            m.Copy(source, OffsetAddress(source, 0x200), history * 8);
            m.Copy(OffsetAddress(source, 0x200 + history * 8), OffsetAddress(source, 0x400 + history * 8), 20 * 4);
            m.Copy(OffsetAddress(c, 56), OffsetAddress(c, 184), m.I32(OffsetAddress(s,DspStateLayout.AnalysisHistoryRadius)) * 8);
            uint historySource = OffsetAddress(buffer, m.I32(OffsetAddress(s,DspStateLayout.InterpolationRatio)) * 0x200);
            m.Copy(buffer, historySource, m.I32(OffsetAddress(s,DspStateLayout.RetainedOutputSamples)) * 4);
            int count = m.I32(OffsetAddress(s,DspStateLayout.DirectOutputOffset));
            if (m.I32(OffsetAddress(s,DspStateLayout.SynthesisMixMode)) != 0 && m.I32(OffsetAddress(s,DspStateLayout.DirectOutputMode)) != 0) count += 256;
            m.Copy(buffer, historySource, count * 4);
        }
    }
}
