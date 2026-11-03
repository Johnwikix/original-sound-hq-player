using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ATL;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    /// <summary>把变更字段写入专辑全部曲目的文件并同步数据库；单个文件被占用或失败不中断其余曲目，返回失败路径。</summary>
    public async Task<IReadOnlyList<string>> WriteAlbumTagsAsync(IReadOnlyList<Music> tracks, AlbumTagEdit edit, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        List<string> failed = [];
        foreach (var music in tracks)
        {
            token.ThrowIfCancellationRequested();
            string path = music.Path;
            if (music.IsRemote) throw new InvalidOperationException(ToolUtils.GetString("WebDavReadOnly"));
            try
            {
                var (tags, updated) = await Task.Run(() => WriteAlbumTagsCore(path, edit, token), token);
                token.ThrowIfCancellationRequested();
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    token.ThrowIfCancellationRequested();
                    db.Execute("UPDATE Music SET Album=?,Year=?,UpdateTime=? WHERE Id=?",
                        edit.Album ?? music.Album, edit.Year ?? music.Year, updated, music.Id);
                    // 文件全量六项可选标签已在上面读出，此处连同修改一并入库并置已读标记。
                    MusicMetadataStorage.Save(db, music.Id, tags);
                });
                // 提交成功后发布事实到内存对象；失败曲目保持原值，用户可重试。
                if (edit.Album is { } album) music.Album = album;
                if (edit.Year is { } year) music.Year = year;
                music.UpdateTime = updated;
                tags.ApplyTo(music);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                // 正在播放等共享冲突不算失败终点；计入失败列表，下一轮保存或退出屏障后重试。
                _logger.LogWarning("专辑标签写入被文件占用，保留待重试: {Path}", path);
                failed.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入专辑标签失败: {Path}", path);
                failed.Add(path);
            }
        }
        return failed;
    }

    /// <summary>在写入线程内完成单文件标签重写；文件句柄互斥探测与标签写入必须在同一线程串行。</summary>
    private static (AdditionalMusicMetadata Tags, DateTime Updated) WriteAlbumTagsCore(string path, AlbumTagEdit edit, CancellationToken token)
    {
        // 登记期间媒体扫描跳过该路径，避免与容器级标签重写的独占句柄互相竞争。
        using var gate = AudioFileWriteGate.BeginWrite(path);
        // 先检查共享锁；暂停播放通常仍持有句柄，必须等真正释放。
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        token.ThrowIfCancellationRequested();
        Settings.FileBufferSize = 1024 * 256;
        Track track = new(path);
        if (edit.Album is { } album) track.Album = album;
        if (edit.Year is { } year) track.Year = year;
        // 从文件读取全量可选标签再覆写改动项；未加载可选项的曲目不会被空快照清掉作曲等信息。
        var tags = AudioMetadataTags.Read(track);
        if (edit.AlbumArtist is { } albumArtist) tags = tags with { AlbumArtist = albumArtist.Trim() };
        if (edit.Genre is { } genre) tags = tags with { Genre = genre.Trim() };
        AudioMetadataTags.Write(track, tags);
        if (edit.Cover is { } cover)
        {
            track.EmbeddedPictures.Clear();
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(cover));
        }
        if (!track.Save()) throw new IOException("Metadata save failed: " + path);
        return (tags, DateTime.Now);
    }

    /// <summary>仅把变更字段写入数据库并同步内存对象；可选标签只更新已读取的曲目，未读旧条目保持懒读文件真相。</summary>
    public async Task SaveAlbumTagsAsync(IReadOnlyList<Music> tracks, AlbumTagEdit edit, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        await _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            foreach (var music in tracks)
            {
                if (edit.Album is not null || edit.Year is not null)
                    db.Execute("UPDATE Music SET Album=?,Year=? WHERE Id=?",
                        edit.Album ?? music.Album, edit.Year ?? music.Year, music.Id);
                if (edit.AlbumArtist is not null || edit.Genre is not null)
                    // 只动已读取可选标签的行；AdditionalMetadataRead=0 的旧库行维持空值+懒读语义。
                    db.Execute("UPDATE Music SET AlbumArtist=?,Genre=? WHERE Id=? AND AdditionalMetadataRead=1",
                        edit.AlbumArtist ?? music.AlbumArtist, edit.Genre ?? music.Genre, music.Id);
            }
        });
        token.ThrowIfCancellationRequested();
        // 与数据库行保持同一条件地发布到内存对象，避免刷新前界面出现库内不存在的值。
        foreach (var music in tracks)
        {
            if (edit.Album is { } album) music.Album = album;
            if (edit.Year is { } year) music.Year = year;
            if ((edit.AlbumArtist is not null || edit.Genre is not null) && music.AdditionalMetadataRead)
            {
                if (edit.AlbumArtist is { } albumArtist) music.AlbumArtist = albumArtist;
                if (edit.Genre is { } genre) music.Genre = genre;
            }
        }
    }
}
