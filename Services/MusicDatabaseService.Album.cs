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
        using var lease = await LibraryOperationGate.EnterAsync(token, reportBusy: false);
        foreach (var target in tracks)
        {
            if (!target.CanEditFile || await _dbConnection.ExecuteScalarAsync<int>(
                "SELECT EXISTS(SELECT 1 FROM Music WHERE Path=? COLLATE NOCASE AND (IsCueContainer=1 OR (CueTrackNo>0 AND IsMissing=0 AND IsDetached=0)))", target.Path) != 0)
                throw new InvalidOperationException(ToolUtils.GetString(target.IsRemote ? "WebDavReadOnly" : "CueSharedSource"));
        }
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
                long committedRevision = 0;
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    token.ThrowIfCancellationRequested();
                    committedRevision = db.ExecuteScalar<long>("UPDATE Music SET Album=COALESCE(?,Album),Year=COALESCE(?,Year),UpdateTime=?,MetadataRevision=MetadataRevision+1 WHERE Id=? RETURNING MetadataRevision",
                        edit.Album, edit.Year, updated, music.Id);
                    // 文件全量六项可选标签已在上面读出，此处连同修改一并入库并置已读标记。
                    MusicMetadataStorage.Save(db, music.Id, tags);
                });
                // 提交成功后发布事实到内存对象；失败曲目保持原值，用户可重试。
                if (edit.Album is { } album) music.Album = album;
                if (edit.Year is { } year) music.Year = year;
                music.MetadataRevision = committedRevision;
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
        if (await ApplyAutomaticSongLinksAsync(tracks) > 0 && AppViewModel is not null)
            await AppViewModel.RefreshSongsSourceAsync();
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
        var revisions = new long[tracks.Count];
        var covers = new string?[tracks.Count];
        if (edit.Cover is { } bytes)
            for (int i = 0; i < tracks.Count; i++)
                covers[i] = await SaveVersionCoverAsync(tracks[i].Id, bytes, token);
        await _dbConnection.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < tracks.Count; i++)
            {
                var music = tracks[i];
                var before = music.IsCueTrack ? db.Get<Music>(music.Id) : null;
                revisions[i] = MusicMetadataStorage.UpdateAlbumFields(db, music.Id, edit.Album, edit.Year, edit.AlbumArtist, edit.Genre);
                if (before is not null && revisions[i] != 0) CueMetadataOverrides.Capture(db, before, db.Get<Music>(music.Id));
                if (covers[i] is { } path)
                    revisions[i] = db.ExecuteScalar<long>("UPDATE Music SET CoverOverridePath=?,ImageHash=?,MetadataRevision=MetadataRevision+1 WHERE Id=? RETURNING MetadataRevision",
                        path, Path.GetFileNameWithoutExtension(path), music.Id);
            }
        });
        for (int i = 0; i < tracks.Count; i++)
        {
            if (revisions[i] == 0) continue;
            var music = tracks[i];
            music.MetadataRevision = revisions[i];
            if (covers[i] is { } path)
            {
                music.CoverOverridePath = path;
                music.ImageHash = Path.GetFileNameWithoutExtension(path);
            }
            if (edit.Album is { } album) music.Album = album;
            if (edit.Year is { } year) music.Year = year;
            if ((edit.AlbumArtist is not null || edit.Genre is not null) && music.AdditionalMetadataRead)
            {
                if (edit.AlbumArtist is { } albumArtist) music.AlbumArtist = albumArtist;
                if (edit.Genre is { } genre) music.Genre = genre;
            }
        }
        if (await ApplyAutomaticSongLinksAsync(tracks) > 0 && AppViewModel is not null)
            await AppViewModel.RefreshSongsSourceAsync();
    }
}
