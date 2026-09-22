using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    public async Task<List<WebDavSource>> GetWebDavSourcesAsync()
    {
        await WhenInitialized;
        return await _dbConnection.Table<WebDavSource>().ToListAsync();
    }
    public Task SaveWebDavSourceAsync(WebDavSource source) => source.Id == 0 ? _dbConnection.InsertAsync(source) : _dbConnection.UpdateAsync(source);
    public Task<RemoteTrack?> GetRemoteTrackAsync(int musicId) => _dbConnection.FindAsync<RemoteTrack>(musicId)!;
    public Task<Music?> GetRemoteMusicAsync(int sourceId, string href) =>
        _dbConnection.FindWithQueryAsync<Music>("SELECT Music.* FROM Music JOIN RemoteTrack ON Music.Id=RemoteTrack.MusicId WHERE RemoteTrack.SourceId=? AND Href=? COLLATE BINARY", sourceId, href)!;
    public async Task<WebDavCacheSettings> GetWebDavCacheSettingsAsync()
    {
        await WhenInitialized;
        return await _dbConnection.FindAsync<WebDavCacheSettings>(1) ?? new();
    }
    public Task SaveWebDavCacheSettingsAsync(WebDavCacheSettings settings) => _dbConnection.InsertOrReplaceAsync(settings);

    public async Task<List<Music>> CommitRemoteEntriesAsync(WebDavSource source, string parent, string run, IReadOnlyList<WebDavEntry> entries)
    {
        var changed = new List<Music>(entries.Count);
        await _dbConnection.RunInTransactionAsync(db =>
        {
            // 删除来源与迟到扫描提交通过数据库事务串行，迟到结果不能重建已移除来源。
            if (db.Find<WebDavSource>(source.Id) is null) return;
            foreach (var entry in entries)
            {
                var remote = db.FindWithQuery<RemoteTrack>("SELECT * FROM RemoteTrack WHERE SourceId = ? AND Href = ? COLLATE BINARY", source.Id, entry.Href);
                var music = remote is null ? null : db.Find<Music>(remote.MusicId);
                if (music is null)
                {
                    music = new Music
                    {
                        SourceId = source.Id, Path = $"webdav://{source.Id}{entry.Href}",
                        Title = Path.GetFileNameWithoutExtension(entry.Name), Extension = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                        FolderPath = $"webdav://{source.Id}{parent}", LastLevelFolderPath = source.Name + " / " + Uri.UnescapeDataString(parent),
                        CreateTime = DateTime.Now, UpdateTime = DateTime.Now
                    };
                    db.Insert(music);
                    remote = new RemoteTrack { MusicId = music.Id, SourceId = source.Id, Href = entry.Href, ParentHref = parent };
                }
                string modified = entry.Modified?.ToString("O") ?? "";
                bool versionChanged = remote!.Length != entry.Length || remote.ETag != entry.ETag || remote.Modified != modified;
                if (music.Extension.StartsWith('.')) { music.Extension = music.Extension.TrimStart('.'); db.Update(music); }
                if (versionChanged || music.ImageHash.Length == 0)
                {
                    remote.MetadataState = "Pending";
                    music.ImageHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Id}\n{entry.Href}\n{entry.ETag}\n{modified}\n{entry.Length}")));
                    db.Update(music);
                }
                remote.Length = entry.Length;
                remote.ETag = entry.ETag;
                remote.Modified = modified;
                remote.SeenRun = run;
                remote.Missing = false;
                db.InsertOrReplace(remote);
                changed.Add(music);
            }
        });
        return changed;
    }

    public Task MarkRemoteDirectoryScannedAsync(int sourceId, string parent, string run) =>
        _dbConnection.ExecuteAsync("UPDATE RemoteTrack SET Missing = 1 WHERE SourceId = ? AND ParentHref = ? COLLATE BINARY AND SeenRun <> ?", sourceId, parent, run);

    public Task MarkRemoteSourceScannedAsync(int sourceId, string run) =>
        _dbConnection.ExecuteAsync("UPDATE RemoteTrack SET Missing = 1 WHERE SourceId = ? AND SeenRun <> ?", sourceId, run);
    public Task RetryDeferredRemoteMetadataAsync(int sourceId) =>
        _dbConnection.ExecuteAsync("UPDATE RemoteTrack SET MetadataState = 'Pending' WHERE SourceId = ? AND MetadataState = 'Deferred'", sourceId);

    public Task<List<RemoteTrack>> GetPendingRemoteMetadataAsync(int sourceId, int afterId) =>
        _dbConnection.QueryAsync<RemoteTrack>("SELECT * FROM RemoteTrack WHERE SourceId = ? AND MusicId > ? AND MetadataState = 'Pending' AND Missing = 0 ORDER BY MusicId LIMIT 64", sourceId, afterId);

    public async Task<Music?> CommitRemoteMetadataAsync(RemoteTrack expected, RemoteMetadata? metadata, string status)
    {
        Music? result = null;
        await _dbConnection.RunInTransactionAsync(db =>
        {
            var remote = db.Find<RemoteTrack>(expected.MusicId);
            if (remote is null || remote.ETag != expected.ETag || remote.Modified != expected.Modified || remote.Length != expected.Length) return;
            var music = db.Find<Music>(expected.MusicId);
            if (music is null) return;
            if (metadata is not null)
            {
                if (metadata.Title.Length != 0) music.Title = metadata.Title;
                music.Author = metadata.Artist;
                music.Album = metadata.Album;
                music.TrackNumber = metadata.Track;
                music.DiskNumber = metadata.Disc;
                music.Year = metadata.Year;
                music.SampleRate = metadata.SampleRate;
                music.Channel = metadata.Channels;
                music.BitDepth = metadata.BitDepth;
                music.BitRate = metadata.BitRate;
                music.Duration = TimeSpan.FromMilliseconds(metadata.DurationMs);
                music.UpdateTime = DateTime.Now;
                // 仅更新元数据，不覆盖在 UI 上并发发生的收藏、统计和歌词偏移。
                db.Execute("UPDATE Music SET Title=?,Author=?,Album=?,TrackNumber=?,DiskNumber=?,Year=?,SampleRate=?,Channel=?,BitDepth=?,BitRate=?,Duration=?,UpdateTime=? WHERE Id=?",
                    music.Title, music.Author, music.Album, music.TrackNumber, music.DiskNumber, music.Year, music.SampleRate, music.Channel,
                    music.BitDepth, music.BitRate, music.Duration, music.UpdateTime, music.Id);
                result = music;
            }
            remote.MetadataState = status;
            db.Update(remote);
        });
        return result;
    }

    public Task RemoveWebDavSourceAsync(int sourceId) => _dbConnection.RunInTransactionAsync(db =>
    {
        foreach (int id in db.QueryScalars<int>("SELECT MusicId FROM RemoteTrack WHERE SourceId = ?", sourceId))
        {
            db.Execute("DELETE FROM PlayListMusic WHERE MusicId = ?", id);
            db.Delete<MusicLyrics>(id);
            db.Delete<Music>(id);
        }
        db.Execute("DELETE FROM RemoteTrack WHERE SourceId = ?", sourceId);
        db.Delete<WebDavSource>(sourceId);
    });
}
