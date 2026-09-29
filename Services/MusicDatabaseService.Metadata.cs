using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    public Task SaveDetailsAsync(Music music, LyricsDocument document, long expectedRevision, CancellationToken token)
    {
        int id = music.Id;
        string title = music.Title, author = music.Author, album = music.Album;
        var track = music.TrackNumber;
        var disk = music.DiskNumber;
        var year = music.Year;
        var updated = music.UpdateTime;
        return _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            LyricsRepository.SaveInTransaction(db, id, document, expectedRevision, "User");
            db.Execute("UPDATE Music SET Title=?,Author=?,Album=?,TrackNumber=?,DiskNumber=?,Year=?,UpdateTime=? WHERE Id=?",
                title, author, album, track, disk, year, updated, id);
        });
    }

    /// <summary>持久化编辑快照，供后台在文件释放后写入；重复编辑替换旧任务。</summary>
    public async Task QueueMetadataWriteAsync(Music music, byte[]? cover, LyricsDocument document, long expectedRevision, CancellationToken token = default)
    {
        if (music.IsRemote) throw new InvalidOperationException(ToolUtils.GetString("WebDavReadOnly"));
        var request = new PendingMetadataWrite
        {
            Path = Path.GetFullPath(music.Path), Title = music.Title, Album = music.Album,
            Author = music.Author, TrackNumber = music.TrackNumber, DiskNumber = music.DiskNumber,
            Year = music.Year, Cover = cover is null ? null : (byte[])cover.Clone(),
            Lyrics = document.Original.Content, LyricsFormat = document.Original.Format, TranslationLrc = document.TranslationLrc, LyricsSchemaVersion = 2
        };
        int musicId = music.Id;
        var updated = music.UpdateTime;
        await _metadataGate.WaitAsync(token);
        try
        {
            await _dbConnection.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                LyricsRepository.SaveInTransaction(db, musicId, document, expectedRevision, "User");
                db.InsertOrReplace(request);
                db.Execute("UPDATE Music SET Title=?,Author=?,Album=?,TrackNumber=?,DiskNumber=?,Year=?,UpdateTime=? WHERE Id=?",
                    request.Title, request.Author, request.Album, request.TrackNumber, request.DiskNumber, request.Year, updated, musicId);
            });
        }
        finally { _metadataGate.Release(); }
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
                    if (request.LyricsSchemaVersion > 2) throw new FormatException("Unsupported queued lyrics schema version.");
                    var parser = new LyricsParser();
                    if (request.LyricsSchemaVersion < 2)
                    {
                        var migrated = LyricsLegacyMigration.Convert(new MusicLyrics { Lyrics = request.Lyrics ?? "", Krc = request.Krc ?? "" }, parser, cancellationToken);
                        request.LegacyLyrics = request.Lyrics;
                        request.Lyrics = migrated.Lyrics;
                        request.LyricsFormat = migrated.LyricsFormat;
                        request.TranslationLrc = migrated.TranslatedLyrics;
                        request.LyricsSchemaVersion = 2;
                        await _dbConnection.UpdateAsync(request);
                    }
                    string export = parser.ExportLrc(new(new(request.Lyrics ?? "", request.LyricsFormat), request.TranslationLrc), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(request.Lyrics) && string.IsNullOrWhiteSpace(export))
                        throw new FormatException("Queued lyrics cannot be converted to timed LRC; the original queue snapshot is retained.");
                    using var write = AudioFileWriteGate.BeginWrite(request.Path);
                    // 先检查共享锁；暂停播放通常仍持有句柄，必须等真正释放。
                    using (new FileStream(request.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    ToolUtils.SaveMetaData(new Music
                    {
                        Title = request.Title, Album = request.Album, Author = request.Author,
                        TrackNumber = request.TrackNumber, DiskNumber = request.DiskNumber, Year = request.Year
                    }, request.Path, request.Cover, export);
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
