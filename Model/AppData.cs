using System.Collections.Generic;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public static class AppData
    {
        public static List<Music> MusicList { get; set; } = new List<Music>();
        public static List<Folder> FolderList { get; set; } = new List<Folder>();
        public static PlayMode PlayMode { get; set; }
        public static int? LastPlayedMusicId { get; set; }
        public static float Volume { get; set; } = 0.5f;
    }
}
