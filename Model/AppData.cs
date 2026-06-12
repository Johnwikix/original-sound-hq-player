using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.Model
{
    public static class AppData
    {
        public static readonly HashSet<string> UnknownAlbums = [
            "未知专辑", "Unknown Album", "Álbum desconocido", "不明なアルバム", "Неизвестный альбом","Unbekanntes Album"
        ];
        public static readonly HashSet<string> UnknownArtists =
        [
            "未知艺术家", "Unknown Artist", "Artista desconocido", "不明なアーティスト", "Неизвестный артист","Unbekannter Künstler"
        ];
        //public static IReadOnlyCollection<Music> allSongs { get; set; } = [];
        public static List<UsbDeviceMusic> MusicOnUsbDevice { get; set; } = [];
        public static List<PlayListMusic> AllPlayListMusics { get; set; } = [];
        public static ObservableCollection<UsbStorageDevice> UsbStorageDevices { get; set; } = [];
        public static UsbStorageDevice UsbStorageDevice { get; set; } = new();
        public static IntPtr HWnd { get; set; } = IntPtr.Zero;
        public static string SystemLanguage { get; set; } = "en";
        //public static bool IsPlayingDetail { get; set; } = false;
        public static Type CurrentPage { get; set; } = typeof(SongListPage);
        public static bool IsPlaying { get; set; } = false;
    }
}
