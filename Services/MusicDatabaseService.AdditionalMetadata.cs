using System;
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
    /// <summary>只为当前属性窗口补读一次本地标签；已知空值不重复打开文件。</summary>
    public async Task<AdditionalMusicMetadata?> ReadAdditionalMetadataAsync(Music music, CancellationToken token)
    {
        if (music.IsRemote || music.AdditionalMetadataRead) return null;
        await WhenInitialized.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        try
        {
            string path = music.Path;
            int id = music.Id;
            if (await _dbConnection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE", path) != 0) return null;
            if (id > 0)
            {
                var current = await _dbConnection.FindAsync<Music>(id);
                if (current is null || !current.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return null;
                if (current.AdditionalMetadataRead) return AdditionalMusicMetadata.From(current);
            }
            var tags = await Task.Run<AdditionalMusicMetadata?>(() =>
            {
                var stamp = new FileInfo(path);
                long length = stamp.Length;
                DateTime modified = stamp.LastWriteTimeUtc;
                var loaded = AudioMetadataTags.Read(new Track(path));
                token.ThrowIfCancellationRequested();
                stamp.Refresh();
                return stamp.Exists && stamp.Length == length && stamp.LastWriteTimeUtc == modified ? loaded : null;
            }, token);
            token.ThrowIfCancellationRequested();
            if (tags is null) return null;
            if (id <= 0) return tags;
            await MusicMetadataStorage.SaveIfUnreadAsync(_dbConnection, id, path, tags.Value);
            var saved = await _dbConnection.FindAsync<Music>(id);
            token.ThrowIfCancellationRequested();
            return saved is { AdditionalMetadataRead: true } && saved.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                ? AdditionalMusicMetadata.From(saved) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "无法补读音乐可选标签: {Path}", music.Path);
            return null;
        }
    }
}
