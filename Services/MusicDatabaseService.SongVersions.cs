using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private async Task SelectSongVersionDatabasePathAsync()
    {
        string original = DbPath;
        string upgraded = Path.Combine(Path.GetDirectoryName(original)!, Path.GetFileNameWithoutExtension(original) + ".song-v1.db");
        if (!File.Exists(upgraded) && File.Exists(original))
        {
            string temporary = upgraded + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var source = new SQLiteAsyncConnection(original);
            try
            {
                // SQLite 备份包含 WAL 中已提交资料；旧库仍可由旧程序读取。
                await source.BackupAsync(temporary);
                File.Move(temporary, upgraded, false);
            }
            finally
            {
                await source.CloseAsync();
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        DbPath = upgraded;
    }

    /// <summary>按歌单出现项移除，保留同歌曲其他位置。</summary>
    public async Task RemovePlaylistEntriesAsync(int playlistId, IReadOnlyList<int> entryIds)
    {
        await _dbConnection.RunInTransactionAsync(db =>
        {
            foreach (int entryId in entryIds) db.Execute("DELETE FROM PlayListMusic WHERE PlayListId=? AND Id=?", playlistId, entryId);
        });
        await GetPlayListMusic();
    }
}
