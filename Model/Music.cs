using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Model
{
    public class Music
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        public byte[] Cover { get; set; }
        public string Author { get; set; }
    }
}
