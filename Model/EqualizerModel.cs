using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    /// <summary>
    /// 单个均衡器频段。Q 值为预留字段：播放端与 IPC 协议目前只传输增益，
    /// 后续协议升级后无需再改预设结构。
    /// </summary>
    public sealed class EqBand
    {
        public double FrequencyHz { get; set; }
        public double GainDb { get; set; }
        public double Q { get; set; } = EqPreset.DefaultQ;
    }

    /// <summary>
    /// 均衡器预设：内置预设、自定义预设、导入/导出文件共用的数据结构（带版本号）。
    /// </summary>
    public sealed class EqPreset
    {
        public const int CurrentVersion = 1;
        /// <summary>与播放端当前 1.0 倍频程带宽等效的 Q 值（预留默认）。</summary>
        public const double DefaultQ = 1.414;
        public const double MinGainDb = -12;
        public const double MaxGainDb = 12;

        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = string.Empty;
        public List<EqBand> Bands { get; set; } = new();
    }
}
