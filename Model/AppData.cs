using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Threading;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public static class AppData
    {
        public static readonly ConcurrentDictionary<string, BitmapImage> albumCoverCache = [];
        //public static WeakImageCache albumCoverCache = new();
        public static readonly HashSet<string> UnknownAlbums = [
                "未知专辑", "Unknown Album", "Álbum desconocido", "不明なアルバム", "Неизвестный альбом"
         ];
        public static readonly HashSet<string> UnknownArtists =
        [
            "未知艺术家", "Unknown Artist", "Artista desconocido", "不明なアーティスト", "Неизвестный артист"
        ];
        public static IReadOnlyCollection<Music> allSongs = [];
        public static List<UsbDeviceMusic> musicOnUsbDevice = [];
        public static List<PlayListMusic> allPlayListMusics = [];
        public static PlayMode PlayMode { get; set; }
        public static int? LastPlayedMusicId { get; set; }
        public static float Volume { get; set; } = 0.5f;
        public static string searchText = string.Empty;
        public static ObservableCollection<UsbStorageDevice> usbStorageDevices = [];
        public static UsbStorageDevice usbStorageDevice = new();
        public static string sortOrder { get; set; } = "DefaultOrder";
        public static IntPtr m_hWnd { get; set; } = IntPtr.Zero;
        public static double AppDpiScale { get; set; } = 1.0;
        public static string systemLanguage { get; set; } = "en";
        public static int MaxSupportedSampleRate = 0;
        public static int MaxSupportedBitDepth = 0;       
    }
}
