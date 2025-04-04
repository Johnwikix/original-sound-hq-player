using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static OutputDevice OutputDevice { get; set; } = new OutputDevice(
            (new MMDeviceEnumerator()).EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)[0]);
        public static string OutputMode { get; set; } = "WasapiExclusive";
        public static int Latency { get; set; } = 400;

        public static event EventHandler OutputSettingsChanged;

        public static List<string> outputDeviceList = new List<string>();

        public static string DeviceName = "Default";
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
