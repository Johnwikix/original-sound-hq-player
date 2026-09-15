using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    /// <summary>持久化编辑快照，供后台在文件释放后写入；重复编辑替换旧任务。</summary>
    public async Task QueueMetadataWriteAsync(Music music, byte[]? cover, string? lyrics, string? krc,
        string? translatedLyrics, string? translatedKrc)
    {
        var request = new PendingMetadataWrite
        {
            Path = Path.GetFullPath(music.Path), Title = music.Title, Album = music.Album,
            Author = music.Author, TrackNumber = music.TrackNumber, DiskNumber = music.DiskNumber,
            Year = music.Year, Cover = cover is null ? null : (byte[])cover.Clone(), Lyrics = lyrics, Krc = krc
        };
        await _metadataGate.WaitAsync();
        try
        {
            await _dbConnection.RunInTransactionAsync(db =>
            {
                db.InsertOrReplace(request);
                db.Update(music);
                db.InsertOrReplace(new MusicLyrics
                {
                    MusicId = music.Id, Lyrics = lyrics ?? "", Krc = krc ?? "",
                    TranslatedLyrics = translatedLyrics ?? "", TKrc = translatedKrc ?? ""
                });
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
                    using var write = AudioFileWriteGate.BeginWrite(request.Path);
                    // 先检查共享锁；暂停播放通常仍持有句柄，必须等真正释放。
                    using (new FileStream(request.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    ToolUtils.SaveMetaData(new Music
                    {
                        Title = request.Title, Album = request.Album, Author = request.Author,
                        TrackNumber = request.TrackNumber, DiskNumber = request.DiskNumber, Year = request.Year
                    }, request.Path, request.Cover, request.Lyrics, request.Krc);
                    await _dbConnection.DeleteAsync(request);
                }
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
                        new NotificationService().SendNotification(ToolUtils.GetString("Error"), request.Path + Environment.NewLine + ex.Message));
                }
            }
        }
        finally { _metadataGate.Release(); }
    }
}
