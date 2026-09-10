using System;
using System.Collections.Generic;
using System.Text.Json;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils
{
    /// <summary>
    /// 均衡器数据结构（EqPreset/EqBand）的序列化、解析与归一化。
    /// 解析 v1 bands 数组（增益与 Q）；缺少 Q 使用默认值，旧频段字典由调用方回退默认预设。
    /// </summary>
    public static class EqualizerHelper
    {
        /// <summary>10 段标准中心频率，与播放端 Equalizer.Frequencies 一致。</summary>
        public static readonly double[] StandardFrequencies =
            { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        public static readonly string[] BuiltInKeys =
            { "Flat", "Rock", "Pop", "Jazz", "Classical", "Electronic", "Vocal" };

        public static string GetBandLabel(double frequencyHz)
        {
            return frequencyHz >= 1000
                ? $"{frequencyHz / 1000:0.#}k"
                : $"{frequencyHz:0.#}";
        }

        /// <summary>频段标签宽度不同（32 vs 16k），按字符宽度对齐时不等宽，仅展示用。</summary>
        public static string FormatGain(double gainDb)
        {
            return gainDb.ToString("+0.0;-0.0;0.0");
        }

        public static EqBand[] CreateDefaultBands()
        {
            var bands = new EqBand[StandardFrequencies.Length];
            for (int i = 0; i < bands.Length; i++)
            {
                bands[i] = new EqBand { FrequencyHz = StandardFrequencies[i], GainDb = 0 };
            }
            return bands;
        }

        public static EqPreset CreatePreset(string name, IReadOnlyList<double> gains, double q = EqPreset.DefaultQ)
        {
            var preset = new EqPreset { Name = name };
            for (int i = 0; i < StandardFrequencies.Length; i++)
            {
                preset.Bands.Add(new EqBand
                {
                    FrequencyHz = StandardFrequencies[i],
                    GainDb = i < gains.Count ? ClampGain(gains[i]) : 0,
                    Q = ClampQ(q)
                });
            }
            return preset;
        }

        /// <summary>深拷贝当前运行时频段状态为可序列化的预设快照。</summary>
        public static EqPreset Snapshot(string name, IReadOnlyList<EqBand> bands)
        {
            var preset = new EqPreset { Name = name };
            foreach (var band in bands)
            {
                preset.Bands.Add(new EqBand
                {
                    FrequencyHz = band.FrequencyHz,
                    GainDb = ClampGain(band.GainDb),
                    Q = ClampQ(band.Q)
                });
            }
            return preset;
        }

        /// <summary>按标准频率补齐/排序频段并夹紧增益，解析外部数据后必须调用。</summary>
        public static EqPreset Normalize(EqPreset? preset)
        {
            preset ??= new EqPreset();
            preset.Version = EqPreset.CurrentVersion;
            preset.Name ??= string.Empty;
            var normalized = new List<EqBand>(StandardFrequencies.Length);
            for (int i = 0; i < StandardFrequencies.Length; i++)
            {
                double freq = StandardFrequencies[i];
                EqBand? match = null;
                foreach (var band in preset.Bands)
                {
                    if (Math.Abs(band.FrequencyHz - freq) <= freq * 0.01)
                    {
                        match = band;
                        break;
                    }
                }
                normalized.Add(new EqBand
                {
                    FrequencyHz = freq,
                    GainDb = ClampGain(match?.GainDb ?? 0),
                    Q = ClampQ(match?.Q ?? EqPreset.DefaultQ)
                });
            }
            preset.Bands = normalized;
            return preset;
        }

        public static double ClampGain(double gainDb)
        {
            return Math.Clamp(gainDb, EqPreset.MinGainDb, EqPreset.MaxGainDb);
        }

        public static double ClampQ(double q) => BassPlayerIpc.Shared.EqParameters.NormalizeQ(q);

        public static double[] GetGains(EqPreset preset)
        {
            var gains = new double[StandardFrequencies.Length];
            for (int i = 0; i < gains.Length; i++) gains[i] = preset.Bands[i].GainDb;
            return gains;
        }

        public static string Serialize(EqPreset preset)
        {
            return JsonSerializer.Serialize(preset, AppJsonSerializerContextHelper.Default.EqPreset);
        }

        /// <summary>解析 EqPreset v1 预设 JSON；旧版频段字典格式不迁移，返回 null 由调用方重置默认。</summary>
        public static EqPreset? Parse(string? json, string fallbackName = "")
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            EqPreset? preset;
            try
            {
                preset = JsonSerializer.Deserialize(json, AppJsonSerializerContextHelper.Default.EqPreset);
            }
            catch (JsonException)
            {
                return null;
            }
            if (preset is not { Bands.Count: > 0 }) return null;
            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? fallbackName : preset.Name;
            return Normalize(preset);
        }

        /// <summary>内置预设（与旧版对话框中的增益表一致）。</summary>
        public static IReadOnlyDictionary<string, EqPreset> CreateBuiltInPresets()
        {
            return new Dictionary<string, EqPreset>
            {
                ["Flat"] = CreatePreset("Flat", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                ["Pop"] = CreatePreset("Pop", new double[] { -1, -0.5, 0, 2, 4, 4, 2, 0, -1, -2 }),
                ["Rock"] = CreatePreset("Rock", new double[] { 4, 3, 2, 1, -0.5, -1, 0, 2, 4, 5 }),
                ["Jazz"] = CreatePreset("Jazz", new double[] { 2, 1, 0, 1, 2, 2, 1, 1, 2, 3 }),
                ["Classical"] = CreatePreset("Classical", new double[] { 3, 2, 1, 0, 0, 0, -1, -1, 1, 2 }),
                ["Electronic"] = CreatePreset("Electronic", new double[] { 3, 2, 0, -1, -0.5, 1, 2, 3, 4, 5 }),
                ["Vocal"] = CreatePreset("Vocal", new double[] { -2, -1, 0, 1, 3, 4, 4, 3, 1, 0 })
            };
        }
    }
}
