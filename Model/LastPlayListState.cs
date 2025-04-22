using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class LastPlayListState
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string PlayListMusicIds { get; set; }
    }
}
