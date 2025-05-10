using SQLite;
using System;

namespace WinUIMusicPlayer.Model
{
    public class SubFolder
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        public DateTime LastModifiedTime { get; set; }
        public int FolderId { get; set; }
    }
}
