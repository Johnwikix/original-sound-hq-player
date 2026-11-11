using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    /// <summary>Commit editor fields and lyrics together without overwriting playback-owned columns.</summary>
    public async Task SaveDetailsAsync(Music music, LyricsDocument document, long expectedRevision, CancellationToken token, bool lyricsChanged = true, int expectedOwnerId = 0, byte[]? cover = null)
    {
        int id = music.Id;
        string title = music.Title, author = music.Author, album = music.Album;
        var track = music.TrackNumber;
        var disk = music.DiskNumber;
        var year = music.Year;
        var updated = music.UpdateTime;
        var additional = music.AdditionalMetadataRead ? AdditionalMusicMetadata.From(music) : (AdditionalMusicMetadata?)null;
        long metadataRevision = music.MetadataRevision;
        string? coverPath = cover is null ? null : await SaveVersionCoverAsync(music.Id, cover, token);
        await _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            LyricsRepository.SaveInTransaction(db, id, document, expectedRevision, "User", lyricsChanged, expectedOwnerId);
            if (db.Execute("UPDATE Music SET Title=?,Author=?,Album=?,TrackNumber=?,DiskNumber=?,Year=?,UpdateTime=?,MetadataRevision=MetadataRevision+1 WHERE Id=? AND MetadataRevision=?",
                title, author, album, track, disk, year, updated, id, metadataRevision) != 1) throw new InvalidOperationException("MusicMetadataConflict");
            if (additional is { } tags) MusicMetadataStorage.Save(db, id, tags);
            if (coverPath is not null) db.Execute("UPDATE Music SET CoverOverridePath=?,ImageHash=? WHERE Id=?", coverPath, Path.GetFileNameWithoutExtension(coverPath), id);
            MusicMetadataStorage.UpdateSongIdentity(db, id, title, author, album);
        });
        music.MetadataRevision = metadataRevision + 1;
        if (coverPath is not null) { music.CoverOverridePath = coverPath; music.ImageHash = Path.GetFileNameWithoutExtension(coverPath); }
        if (await ApplyAutomaticSongLinksAsync([music]) > 0 && AppViewModel is not null)
            await AppViewModel.RefreshSongsSourceAsync();
    }

    /// <summary>持久化编辑快照，供后台在文件释放后写入；重复编辑替换旧任务。</summary>
    public async Task QueueMetadataWriteAsync(Music music, byte[]? cover, LyricsDocument document, long expectedRevision, CancellationToken token = default, bool lyricsChanged = true, int expectedOwnerId = 0)
    {
        if (music.IsRemote) throw new InvalidOperationException(ToolUtils.GetString("WebDavReadOnly"));
        var identity = await Task.Run(() =>
        {
            var file = new FileInfo(music.Path);
            if (!file.Exists) throw new FileNotFoundException(ToolUtils.GetString("SongVersionsFileChanged"), music.Path);
            return (file.Length, file.LastWriteTimeUtc.Ticks);
        }, token);
        var request = new PendingMetadataWrite
        {
            MusicId = music.Id, FileLength = identity.Length, FileLastWriteTicks = identity.Ticks,
            Path = Path.GetFullPath(music.Path), Title = music.Title, Album = music.Album,
            Author = music.Author, TrackNumber = music.TrackNumber, DiskNumber = music.DiskNumber,
            AlbumArtist = music.AlbumArtist, Genre = music.Genre, Composer = music.Composer, Lyricist = music.Lyricist, Conductor = music.Conductor, CatalogNumber = music.CatalogNumber,
            MetadataVersion = music.AdditionalMetadataRead ? 1 : 0,
            Year = music.Year, Cover = cover is null ? null : (byte[])cover.Clone(),
            Lyrics = document.Original.Content, LyricsFormat = document.Original.Format, TranslationLrc = document.TranslationLrc,
            PronunciationLrc = document.PronunciationLrc, LyricsSchemaVersion = 2
        };
        int musicId = music.Id;
        long metadataRevision = music.MetadataRevision;
        string? coverPath = cover is null ? null : await SaveVersionCoverAsync(music.Id, cover, token);
        var updated = music.UpdateTime;
        if (!lyricsChanged && !string.IsNullOrWhiteSpace(document.Original.Content))
            request.PreserveFileLyrics = await Task.Run(() => !new LyricsParser().HasLyrics(document, token), token);
        await _metadataGate.WaitAsync(token);
        try
        {
            await _dbConnection.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                LyricsRepository.SaveInTransaction(db, musicId, document, expectedRevision, "User", lyricsChanged, expectedOwnerId);
                var previous = db.Find<PendingMetadataWrite>(request.Path);
                if (request.Cover is null && previous is not null && previous.MusicId == request.MusicId &&
                    previous.FileLength == request.FileLength && previous.FileLastWriteTicks == request.FileLastWriteTicks)
                    request.Cover = previous.Cover;
                // 未补读的窗口不能用空快照替换已经排队的新标签。
                if (request.MetadataVersion == 0 && previous is { MetadataVersion: 1 })
                {
                    request.MetadataVersion = 1;
                    request.AlbumArtist = previous.AlbumArtist;
                    request.Genre = previous.Genre;
                    request.Composer = previous.Composer;
                    request.Lyricist = previous.Lyricist;
                    request.Conductor = previous.Conductor;
                    request.CatalogNumber = previous.CatalogNumber;
                }
                db.InsertOrReplace(request);
                if (db.Execute("UPDATE Music SET Title=?,Author=?,Album=?,TrackNumber=?,DiskNumber=?,Year=?,UpdateTime=?,MetadataRevision=MetadataRevision+1 WHERE Id=? AND MetadataRevision=?",
                    request.Title, request.Author, request.Album, request.TrackNumber, request.DiskNumber, request.Year, updated, musicId, metadataRevision) != 1)
                    throw new InvalidOperationException("MusicMetadataConflict");
                if (request.MetadataVersion == 1)
                    MusicMetadataStorage.Save(db, musicId, new(request.AlbumArtist, request.Genre, request.Composer, request.Lyricist, request.Conductor, request.CatalogNumber));
                if (coverPath is not null) db.Execute("UPDATE Music SET CoverOverridePath=?,ImageHash=? WHERE Id=?", coverPath, Path.GetFileNameWithoutExtension(coverPath), musicId);
                MusicMetadataStorage.UpdateSongIdentity(db, musicId, request.Title, request.Author, request.Album);
            });
            music.MetadataRevision = metadataRevision + 1;
            if (coverPath is not null) { music.CoverOverridePath = coverPath; music.ImageHash = Path.GetFileNameWithoutExtension(coverPath); }
        }
        finally { _metadataGate.Release(); }
        if (await ApplyAutomaticSongLinksAsync([music]) > 0 && AppViewModel is not null)
            await AppViewModel.RefreshSongsSourceAsync();
    }

    private async Task<string> SaveVersionCoverAsync(int musicId, byte[] bytes, CancellationToken token)
    {
        string root = Path.Combine(Path.GetDirectoryName(_dbConnection.DatabasePath)!, "VersionCovers");
        return await Task.Run(async () =>
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, musicId + "-" + Convert.ToHexString(SHA256.HashData(bytes)) + ".bin");
            if (File.Exists(path)) return path;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path)) { }
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token);
    }

    /// <summary>串行尝试持久化任务，仅在标签保存成功后删除任务。</summary>
    public async Task FlushMetadataWritesAsync(CancellationToken cancellationToken)
    {
        await _metadataGate.WaitAsync(cancellationToken);
        try
        {
            var requests = await _dbConnection.Table<PendingMetadataWrite>().ToListAsync();
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (request.MetadataVersion > 1) throw new FormatException("Unsupported queued metadata version.");
                    if (request.LyricsSchemaVersion > 2) throw new FormatException("Unsupported queued lyrics schema version.");
                    var parser = new LyricsParser();
                    if (request.LyricsSchemaVersion < 2)
                    {
                        var migrated = LyricsLegacyMigration.Convert(new MusicLyrics { Lyrics = request.Lyrics ?? "", Krc = request.Krc ?? "" }, parser, cancellationToken);
                        request.LegacyLyrics = request.Lyrics;
                        request.Lyrics = migrated.Lyrics;
                        request.LyricsFormat = migrated.LyricsFormat;
                        request.TranslationLrc = migrated.TranslatedLyrics;
                        request.PronunciationLrc = null;
                        request.LyricsSchemaVersion = 2;
                        await _dbConnection.UpdateAsync(request);
                    }
                    string? export = null;
                    if (!request.PreserveFileLyrics)
                    {
                        export = parser.ExportLrc(new(new(request.Lyrics ?? "", request.LyricsFormat), request.TranslationLrc, request.PronunciationLrc), cancellationToken);
                        if (!string.IsNullOrWhiteSpace(request.Lyrics) && string.IsNullOrWhiteSpace(export))
                            throw new FormatException("Queued lyrics cannot be converted to timed LRC; the original queue snapshot is retained.");
                    }
                    using var write = AudioFileWriteGate.BeginWrite(request.Path);
                    var version = await _dbConnection.FindAsync<Music>(request.MusicId);
                    var file = new FileInfo(request.Path);
                    if (request.MusicId <= 0 || version is null || version.IsDetached ||
                        !string.Equals(Path.GetFullPath(version.Path), request.Path, StringComparison.OrdinalIgnoreCase) ||
                        !file.Exists || file.Length != request.FileLength || file.LastWriteTimeUtc.Ticks != request.FileLastWriteTicks)
                        throw new InvalidOperationException(ToolUtils.GetString("SongVersionsFileChanged"));
                    // 先检查共享锁；暂停播放通常仍持有句柄，必须等真正释放。
                    using (new FileStream(request.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    ToolUtils.SaveMetaData(new Music
                    {
                        Title = request.Title, Album = request.Album, Author = request.Author,
                        AlbumArtist = request.AlbumArtist, Genre = request.Genre, Composer = request.Composer, Lyricist = request.Lyricist, Conductor = request.Conductor, CatalogNumber = request.CatalogNumber,
                        AdditionalMetadataRead = request.MetadataVersion == 1,
                        TrackNumber = request.TrackNumber, DiskNumber = request.DiskNumber, Year = request.Year
                    }, request.Path, request.Cover, export, preserveLyrics: request.PreserveFileLyrics);
                    await _dbConnection.DeleteAsync(request);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                {
                    // 文件占用不算失败；保留数据库任务，下轮继续，无播放热路径分配。
                }
                catch (Exception ex)
                {
                    if (request.LastError == ex.Message) continue;
                    request.LastError = ex.Message;
                    await _dbConnection.UpdateAsync(request);
                    _logger.LogError(ex, "标签写入待重试: {Path}", request.Path);
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        App.Services.GetRequiredService<NotificationService>().SendNotification(ToolUtils.GetString("Error"), request.Path + Environment.NewLine + ex.Message));
                }
            }
        }
        finally { _metadataGate.Release(); }
    }
}
