using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class SaveSettings
    {
        [PrimaryKey]
        public int Id { get; set; } = 1;
        public string OutputMode { get; set; } = "WasapiExclusive";
        public int Latency { get; set; } = 200;
        public string Name { get; set; }
        public string DeviceId { get; set; }
        // 由于 MMDevice 无法直接存储，这里可以只存储关键信息，如设备友好名称
        public string DeviceFriendlyName { get; set; }
    }
}
