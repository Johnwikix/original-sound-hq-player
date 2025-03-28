using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class PlayState
    {
        [PrimaryKey]
        public int Id { get; set; } = 1; // 固定 ID 为 1，方便管理
        public PlayMode PlayMode { get; set; }
        public int? LastPlayedMusicId { get; set; }
    }
}
