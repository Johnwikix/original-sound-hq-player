using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;

namespace WebDavTreeUiRegression;

public sealed partial class TestApp
{
    private static async Task CheckAutomaticRemoteAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "automatic-remote-" + Guid.NewGuid().ToString("N") + ".db");
        var sqlite = new SQLiteAsyncConnection(path);
        try
        {
            await sqlite.CreateTableAsync<Music>();
            await sqlite.CreateTableAsync<RemoteTrack>();
            await sqlite.CreateTableAsync<WebDavSource>();
            await sqlite.CreateTableAsync<MusicLyrics>();
            await sqlite.CreateTableAsync<PlayListMusic>();
            var local = new Music { Title = "Song", Author = "Artist", Album = "Album", Path = "fixture.flac", IsFavorite = true };
            await sqlite.InsertAsync(local);
            var db = new MusicDatabaseService(sqlite) { Versions = new SongVersionStore(sqlite) };
            await db.Versions.InitializeAsync(path);
            await db.Lyrics.InitializeAsync(path);
            await db.Versions.ApplyAutomaticLinksAsync(db.Lyrics);
            var source = new WebDavSource { Name = "Fixture", BaseUri = "http://localhost/dav/", Roots = "/dav/" };
            await sqlite.InsertAsync(source);
            var batch = await db.CommitRemoteEntriesAsync(source, "/dav/", "run", [new("/dav/song.mp3", "song.mp3", false, 10, "v1", null)]);
            var remote = batch.Batch.Single();
            Check((await db.Versions.GetVersionsAsync(local.Id)).Count == 1, "remote filenames without artist/album do not auto-link prematurely");
            var expected = (await sqlite.FindAsync<RemoteTrack>(remote.Id))!;
            var tagged = await db.CommitRemoteMetadataAsync(expected, new RemoteMetadata(" song ", "ARTIST", "Album", 1, 1, 2001, 44100, 2, 16, 320, 1000), "Complete");
            Check(tagged is not null && tagged.SongId == (await db.Versions.GetSongAsync(local.Id))!.Id && tagged.IsFavorite,
                "real remote metadata transaction auto-links to local audio and returns shared projections");
            Check((await db.Versions.GetVersionsAsync(local.Id)).Count == 2 && (await sqlite.FindAsync<RemoteTrack>(remote.Id))!.Href == expected.Href,
                "remote automatic linking preserves the remote resource identity");
            await db.MarkRemoteSourceScannedAsync(source.Id, "different-run");
            Check((await db.Versions.GetVersionsAsync(local.Id)).Count == 2 && (await db.Versions.GetSongAsync(remote.Id))!.IsFavorite,
                "missing remote file retains its automatically associated song and favorite");
            Check((await db.GetVisibleRemoteTracksAsync()).Single().MusicId == remote.Id,
                "a missing attached remote track remains eligible for the complete-cache check");
            await db.Versions.RemoveMissingVersionAsync(remote.Id);
            Check((await db.GetVisibleRemoteTracksAsync()).Count == 0 &&
                (await db.Versions.GetVersionsAsync(local.Id)).Single().Id == local.Id,
                "explicit removal drops the remote cache-status lookup while keeping the local version");
        }
        finally
        {
            await sqlite.CloseAsync();
            File.Delete(path);
            foreach (string suffix in new[] { ".song-v1.bak", ".lyrics-v1.bak", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
