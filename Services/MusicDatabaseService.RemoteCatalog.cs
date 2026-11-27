using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    internal static async Task EnsureRemoteCatalogSchemaAsync(SQLiteAsyncConnection db, bool upgrading)
    {
        await db.CreateTableAsync<WebDavSource>().ConfigureAwait(false);
        await db.CreateTableAsync<RemoteTrack>().ConfigureAwait(false);
        await db.CreateTableAsync<WebDavCacheSettings>().ConfigureAwait(false);
        if (upgrading)
            await db.ExecuteAsync("UPDATE WebDavSource SET Kind=0,ConfigurationRevision=0 WHERE Kind IS NULL").ConfigureAwait(false);
        await db.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_RemoteTrack_Resource ON RemoteTrack(SourceId, Href COLLATE BINARY)")
            .ConfigureAwait(false);
    }

    internal async Task<List<Music>> CommitCatalogTracksAsync(WebDavSource source, string run, IReadOnlyList<CatalogTrack> tracks,
        CancellationToken token)
    {
        var changed = new List<Music>(tracks.Count);
        await _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            RequireCurrentCatalog(db, source);
            string root = $"remote://{source.Id.ToString(CultureInfo.InvariantCulture)}/";
            foreach (var track in tracks)
            {
                token.ThrowIfCancellationRequested();
                var remote = db.FindWithQuery<RemoteTrack>(
                    "SELECT * FROM RemoteTrack WHERE SourceId=? AND Href=? COLLATE BINARY", source.Id, track.Id);
                var music = remote is null ? null : db.Find<Music>(remote.MusicId);
                string folder = track.AlbumId.Length == 0 ? root : root + Uri.EscapeDataString(track.AlbumId);
                string folderName = track.AlbumId.Length == 0 ? source.Name : source.Name + " / " + track.Album;
                string path = remote?.ResourcePath ?? root + "track/" + Uri.EscapeDataString(track.Id);
                if (music is null)
                {
                    music = new Music { SourceId = source.Id, Path = path, CreateTime = DateTime.Now };
                    ApplyCatalogTrack(music, track, folder, folderName, CoverHash(source.Id, track));
                    db.Insert(music);
                    MusicMetadataStorage.AttachSong(db, music);
                    remote = new RemoteTrack { MusicId = music.Id, SourceId = source.Id, Href = track.Id, ResourcePath = path };
                    changed.Add(music);
                }
                else
                {
                    string coverHash = CoverHash(source.Id, track);
                    if (CatalogMusicChanged(music, track, folder, folderName, coverHash))
                    {
                        ApplyCatalogTrack(music, track, folder, folderName, coverHash);
                        db.Update(music);
                        changed.Add(music);
                    }
                }
                remote!.Length = track.Length;
                remote.ParentHref = track.AlbumId;
                remote.Extension = track.Extension;
                remote.CoverId = track.CoverId;
                remote.MetadataState = "Complete";
                remote.SeenRun = run;
                remote.Missing = false;
                db.InsertOrReplace(remote);
            }
        }).ConfigureAwait(false);
        // The batch is already durable. Finish publishing song ownership even when the scan is cancelled here.
        if (changed.Count != 0) await ApplyAutomaticSongLinksAsync(changed).ConfigureAwait(false);
        return changed;
    }

    internal async Task<int> CompleteCatalogSyncAsync(WebDavSource source, string run, CancellationToken token)
    {
        int missing = 0;
        await _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            RequireCurrentCatalog(db, source);
            db.Execute("UPDATE Music SET IsMissing=1 WHERE Id IN (SELECT MusicId FROM RemoteTrack WHERE SourceId=? AND SeenRun<>? AND Missing=0)",
                source.Id, run);
            missing = db.Execute("UPDATE RemoteTrack SET Missing=1 WHERE SourceId=? AND SeenRun<>? AND Missing=0", source.Id, run);
            db.Execute("UPDATE WebDavSource SET LastScanUtc=? WHERE Id=?", DateTime.UtcNow, source.Id);
        }).ConfigureAwait(false);
        return missing;
    }

    private static void RequireCurrentCatalog(SQLiteConnection db, WebDavSource source)
    {
        var current = db.Find<WebDavSource>(source.Id);
        if (current is null || !current.Enabled || current.Kind != source.Kind || current.ConfigurationRevision != source.ConfigurationRevision)
            throw new WebDavException("SourceUnavailable");
    }

    private static bool CatalogMusicChanged(Music music, CatalogTrack track, string folder, string folderName, string cover) =>
        music.Title != track.Title || music.Author != track.Artist || music.Album != track.Album ||
        music.AlbumArtist != track.AlbumArtist || music.Genre != track.Genre || music.TrackNumber != track.TrackNumber ||
        music.DiskNumber != track.DiscNumber || music.Year != track.Year || music.Duration != track.Duration ||
        music.Extension != track.Extension || music.BitRate != track.BitRate || music.SampleRate != track.SampleRate ||
        music.Channel != track.Channels || music.BitDepth != track.BitDepth || music.FolderPath != folder ||
        music.LastLevelFolderPath != folderName || music.ImageHash != cover || music.IsMissing || music.IsDetached;

    private static void ApplyCatalogTrack(Music music, CatalogTrack track, string folder, string folderName, string cover)
    {
        music.Title = track.Title;
        music.TitleFromFileName = false;
        music.Author = track.Artist;
        music.Album = track.Album;
        music.AlbumArtist = track.AlbumArtist;
        music.Genre = track.Genre;
        music.TrackNumber = track.TrackNumber;
        music.DiskNumber = track.DiscNumber;
        music.Year = track.Year;
        music.Duration = track.Duration;
        music.Extension = track.Extension;
        music.BitRate = track.BitRate;
        music.SampleRate = track.SampleRate;
        music.Channel = track.Channels;
        music.BitDepth = track.BitDepth;
        music.FolderPath = folder;
        music.LastLevelFolderPath = folderName;
        music.ImageHash = cover;
        music.AdditionalMetadataRead = true;
        music.IsMissing = false;
        music.IsDetached = false;
        music.MetadataRevision++;
        music.UpdateTime = DateTime.Now;
    }

    private static string CoverHash(int sourceId, CatalogTrack track) => track.CoverId.Length == 0 ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}\n{track.CoverId}\n{track.CoverVersion}")));
}
