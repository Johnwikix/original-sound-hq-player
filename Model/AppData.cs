using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public static class AppData
    {
        public static Dictionary<string, BitmapImage> albumCoverCache = new Dictionary<string, BitmapImage>();

        public static List<Music> allSongs = new List<Music>();

        public static List<UsbDeviceMusic> musicOnUsbDevice = new List<UsbDeviceMusic>();

        public static List<PlayListMusic> allPlayListMusics = new List<PlayListMusic>();
        public static PlayMode PlayMode { get; set; }
        public static int? LastPlayedMusicId { get; set; }
        public static float Volume { get; set; } = 0.5f;
        public static string searchText = string.Empty;
        public static List<UsbStorageDevice> usbStorageDevices = new List<UsbStorageDevice>();
        public static UsbStorageDevice usbStorageDevice = new UsbStorageDevice();
        public static string sortOrder { get; set; } = "DefaultOrder";
        public static IntPtr m_hWnd { get; set; } = IntPtr.Zero;
        public static bool IsEqualizerEnabled { get; set; } = false;
        public static Dictionary<string, double> equalizer = new Dictionary<string, double>
        {
            {"32Hz", 0},   // 32Hz 初始增益 0dB
            {"64Hz", 0},   // 64Hz 初始增益 0dB
            {"125Hz", 0},  // 125Hz 初始增益 0dB
            {"250Hz", 0},  // 250Hz 初始增益 0dB
            {"500Hz", 0},  // 500Hz 初始增益 0dB
            {"1kHz", 0},   // 1kHz 初始增益 0dB
            {"2kHz", 0},   // 2kHz 初始增益 0dB
            {"4kHz", 0},   // 4kHz 初始增益 0dB
            {"8kHz", 0},   // 8kHz 初始增益 0dB
            {"16kHz", 0}   // 16kHz 初始增益 0dB
        };
        //public static PlayMode currentPlayMode = PlayMode.ListLoop;
    }
}
