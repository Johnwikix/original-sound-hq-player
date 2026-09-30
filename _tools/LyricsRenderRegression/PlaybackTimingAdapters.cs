using WinUIMusicPlayer.Model;

// The native test uses real sidecar IO and the production parser/projection. These
// dependencies are outside that path and fail if source selection unexpectedly calls them.
namespace WinUIMusicPlayer { internal static class AppData { public static string SystemLanguage => "en-US"; } }
namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public bool IsRemote => false;
        public string? EmbeddedLyrics => null;
    }
    internal static class AppSettings
    {
        public static string LocalLyricsFormatOrder => "krc,qrc,lrc,ttml";
        public static bool PreferDatabaseLyrics => false;
        public static bool IsAutoLyricsEnabled => false;
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService { public UnusedLyricsRepository Lyrics { get; } = new(); }
    public sealed class UnusedLyricsRepository
    {
        public Task<LyricsSnapshot> GetAsync(int id, CancellationToken token) => throw new InvalidOperationException("Unexpected database read");
        public Task<bool> SaveAsync(int id, LyricsDocument doc, long revision, string kind, string key = "", CancellationToken token = default) => throw new InvalidOperationException("Unexpected database write");
    }
    public sealed class WebDavLibraryService
    {
        public Task<LyricsDocument?> ReadLyricsDocumentAsync(Music music, string order, CancellationToken token) => throw new InvalidOperationException("Unexpected WebDAV access");
    }
    internal static class OneShotLyricsCache
    {
        public static Lyrics.LyricsCacheEntry? Load(string path) => throw new InvalidOperationException("Unexpected cache read");
        public static bool TrySave(string path, LyricsDocument document, long revision) => throw new InvalidOperationException("Unexpected cache write");
    }
}
namespace WinUIMusicPlayer.Services.Lyrics
{
    // The production resolver requires the cache record's name as its boundary type.
    internal sealed record LyricsCacheEntry(LyricsDocument Document, long Revision, string SourceKind);
    public sealed class LyricsOnlineSearch
    {
        public Task<LyricsDocument?> SearchAsync(Music music, bool force, CancellationToken token) => throw new InvalidOperationException("Unexpected online search");
    }
}
