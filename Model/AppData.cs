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

        //public static PlayMode currentPlayMode = PlayMode.ListLoop;
    }
}
