using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class PlayListMusic
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int PlayListId { get; set; }
        public int MusicId { get; set; }
        public int Order { get; set; }
    }
}
