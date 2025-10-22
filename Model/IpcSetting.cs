using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class IpcSetting
    {
        public string PlayMode { get; set; } = "ListLoop";
        public string OutputMode { get; set; } = "DirectSound";
        public int BassOutputDeviceId { get; set; } = -1;
        public int BassASIODeviceId { get; set; } = 0;
        public int Latency { get; set; } = 400;
        public bool IsDopEnabled { get; set; } = false;
        public int dsdGain { get; set; } = 6;
        public int dsdPcmFreq { get; set; } = 88200;
        public bool IsEqualizerEnabled { get; set; } = false;
        public float Volume { get; set; } = 0.5f;
        public bool IsSettingChanged { get; set; } = false;
    }
}
