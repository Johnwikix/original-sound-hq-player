using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class Folder
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
    }
}
