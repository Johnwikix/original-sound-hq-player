using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    /// <summary>
    /// 单个均衡器频段：增益和 Q 值均参与播放端滤波器计算。
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
        /// <summary>峰值滤波器默认 Q，低频近似一倍频程带宽。</summary>
        public const double DefaultQ = BassPlayerIpc.Shared.EqParameters.DefaultQ;
        public const double MinQ = BassPlayerIpc.Shared.EqParameters.MinQ;
        public const double MaxQ = BassPlayerIpc.Shared.EqParameters.MaxQ;
        public const double MinGainDb = -12;
        public const double MaxGainDb = 12;

        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = string.Empty;
        public List<EqBand> Bands { get; set; } = new();
    }
}
