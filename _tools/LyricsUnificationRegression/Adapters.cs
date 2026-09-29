using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

// Only external providers/application storage are substituted. Parsing, local file IO,
// SQLite repository, cache persistence and render models are production code.
namespace WinUIMusicPlayer
{
    public static class AppData { public static string SystemLanguage = "en-US"; }
}
namespace WinUIMusicPlayer.Model
{
    public class Music
    {
        [PrimaryKey] public int Id { get; set; }
        public int SourceId { get; set; }
        [Ignore] public bool IsRemote => SourceId != 0;
        public string Path { get; set; } = "";
        public string Title { get; set; } = "Test";
        public string Author { get; set; } = "Author";
        public string Album { get; set; } = "Album";
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
        public string? EmbeddedLyrics { get; set; }
        public bool IsLrcSearched { get; set; }
        public bool IsKrcSearched { get; set; }
    }
    public static class AppSettings
    {
        public static string LocalLyricsFormatOrder = "krc,qrc,lrc,ttml";
        public static bool IsAutoLyricsEnabled = true;
    }
}
namespace WinUIMusicPlayer.Services
{
    public class MusicDatabaseService(SQLiteAsyncConnection db, LyricsParser parser)
    {
        public LyricsRepository Lyrics { get; } = new(db, parser);
        public SQLiteAsyncConnection GetDbConnection() => db;
        public Func<Task>? BeforeSave;
        public async Task SaveDetailsAsync(Music music, LyricsDocument document, long revision, CancellationToken token)
        {
            if (BeforeSave is not null) await BeforeSave();
            if (!await Lyrics.SaveAsync(music.Id, document, revision, "User", token: token)) throw new InvalidOperationException("LyricsEditConflict");
        }
        public Task QueueMetadataWriteAsync(Music music, byte[]? cover, LyricsDocument document, long revision, CancellationToken token) => SaveDetailsAsync(music, document, revision, token);
    }
    public class WebDavLibraryService
    {
        public LyricsDocument? Result;
        public Task<LyricsDocument?> ReadLyricsDocumentAsync(Music music, string order, CancellationToken token) => Task.FromResult(Result);
    }
    public static class OneShotLyricsCache
    {
        private static readonly Dictionary<string, LyricsCacheEntry> Data = [];
        public static LyricsCacheEntry? Load(string path) => Data.GetValueOrDefault(path);
        public static void Save(string path, LyricsDocument document) => Data[path] = new() { Document = document, Path = path, Revision = (Load(path)?.Revision ?? 0) + 1 };
        public static bool SaveEdited(string path, LyricsDocument document, long revision) => TrySave(path, document, revision);
        public static bool TrySave(string path, LyricsDocument document, long revision)
        {
            if ((Load(path)?.Revision ?? 0) != revision) return false;
            Save(path, document); return true;
        }
    }
}
namespace WinUIMusicPlayer.Services.Lyrics
{
    public class LyricsOnlineSearch
    {
        public int Calls;
        public Func<Music, bool, CancellationToken, Task<LyricsDocument?>> Handler = (_, _, _) => Task.FromResult<LyricsDocument?>(null);
        public Task<LyricsDocument?> SearchAsync(Music music, bool force, CancellationToken token)
        {
            Calls++;
            return Handler(music, force, token);
        }
    }
}
