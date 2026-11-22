using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    internal async Task<bool> ReconcileLocalCoversAsync(string directory, CancellationToken token, IReadOnlyList<string>? candidatePaths = null)
    {
        if (!Directory.Exists(directory)) return false;
        string revision = await Task.Run(() => LocalCoverSource.GetRevision(directory, true, token, candidatePaths), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await _dbConnection.ExecuteAsync("""
            UPDATE Music SET CoverSourceStamp=?, ImageHash=CASE WHEN coalesce(CoverOverridePath,'')='' THEN '' ELSE ImageHash END
            WHERE SourceId=0 AND FolderPath=? COLLATE NOCASE AND coalesce(CoverSourceStamp,'')<>?
            """, revision, directory, revision).ConfigureAwait(false) > 0;
    }

    internal async Task<bool> UpdateCoverSourceAsync(Music music, string expectedStamp, string expectedOverride, string revision, string imageHash, long expectedAudioTicks, long audioTicks) =>
        await _dbConnection.ExecuteAsync("""
            UPDATE Music SET CoverSourceStamp=?,ImageHash=?,CoverAudioWriteTicks=? WHERE Id=? AND Path=? AND
            coalesce(CoverOverridePath,'')=? AND coalesce(CoverSourceStamp,'') IN (?,?) AND coalesce(CoverAudioWriteTicks,0) IN (?,?)
            """, revision, imageHash, audioTicks, music.Id, music.Path, expectedOverride, expectedStamp, revision, expectedAudioTicks, audioTicks).ConfigureAwait(false) != 0;
}
