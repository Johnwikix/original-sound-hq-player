namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public int SourceId { get; set; }
        public string FolderPath { get; set; } = "";
        public string LastLevelFolderPath { get; set; } = "";
    }
    public sealed class Folder
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsExternalImport { get; set; }
    }
}
namespace WinUIMusicPlayer.Services
{
    public static class LibraryQueries
    {
        public static bool MatchesSource(Model.Music music, int sourceId)
            => sourceId == -1 || (sourceId == -2 ? music.SourceId != 0 : music.SourceId == sourceId);
    }
}
