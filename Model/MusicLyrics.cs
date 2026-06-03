using SQLite;

namespace WinUIMusicPlayer.Model
{
    [Table("MusicLyrics")]
    public class MusicLyrics
    {
        [PrimaryKey]
        public int MusicId { get; set; }
        public string Lyrics { get; set; } = "";
        public string TranslatedLyrics { get; set; } = "";
        public string Krc { get; set; } = "";
        public string TKrc { get; set; } = "";
    }
}
