using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class PlayList
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
