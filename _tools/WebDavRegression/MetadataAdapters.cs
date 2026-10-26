namespace WinUIMusicPlayer.Model;

// These tests use remote metadata values only; no playback or UI model is substituted.
public sealed class Music
{
        public string AlbumArtist { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Composer { get; set; } = "";
        public string Lyricist { get; set; } = "";
        public string Conductor { get; set; } = "";
        public string CatalogNumber { get; set; } = "";
        public bool AdditionalMetadataRead { get; set; }
}
