using System.Collections.Generic;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
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
