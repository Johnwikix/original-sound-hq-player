using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class LastPlayListState
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string PlayListMusicIds { get; set; }
    }
}
