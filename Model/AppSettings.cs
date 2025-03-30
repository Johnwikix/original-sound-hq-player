using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.Design;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static OutputDevice OutputDevice { get; set; } = new OutputDevice(
            "Defualt",
            "0", 
            (new MMDeviceEnumerator()).EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)[0]);
        public static string OutputMode { get; set; } = "WasapiExclusive";
        public static int Latency { get; set; } = 200;

        public static event EventHandler OutputSettingsChanged;
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
