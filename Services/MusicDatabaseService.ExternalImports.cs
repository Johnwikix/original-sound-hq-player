using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    /// <summary>
    /// 外部打开文件入库（OneShotPlaybackService 在固定本地盘且库内无同路径行时调用）：
    /// 先确保"外部导入"虚拟文件夹行存在，再复用提交批核心（PendingMetadataWrite 检查 +
    /// 事务内同路径二次查重，防并发扫描/转换重复落库）。返回权威库内行；并发方先落库时按路径
    /// 回查；失败返回 null 由调用方回退一次性播放。与 AddConvertedFileAsync 同模式，不取
    /// LibraryOperationGate——单行小事务，避免与扫描互锁。
    /// </summary>
    public async Task<Music?> AddExternalFileAsync(Music music, string lyrics)
    {
        await _rescanfolderSemaphore.WaitAsync();
        try
        {
            await EnsureExternalFolderAsync();
            var committed = await CommitScanBatchAsync([(music, lyrics)], null);
            // Insert 已回填自增 Id；被跳过说明并发方先落库（或文件正在被转换写入），按路径取权威行。
            if (committed.Added.Count > 0) return music;
            return await FindMusicByPathAsync(music.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"AddExternalFileAsync 外部文件入库失败: {music.Path}: {ex.Message}");
            try { return await FindMusicByPathAsync(music.Path); }
            catch { return null; }
        }
        finally
        {
            _rescanfolderSemaphore.Release();
        }
    }

    /// <summary>在同一事务内查找并创建虚拟文件夹，避免并发导入各自查空后插入重复行。
    /// Name 存稳定代码值，显示名由界面层注入资源文案。</summary>
    private Task EnsureExternalFolderAsync() => _dbConnection.RunInTransactionAsync(db =>
    {
        int existing = db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Folder WHERE Type = ? AND Path = ?",
            Folder.TypeExternal, Folder.ExternalPath);
        if (existing != 0) return;
        db.Insert(new Folder
        {
            Name = Folder.TypeExternal,
            Path = Folder.ExternalPath,
            Type = Folder.TypeExternal
        });
    });

}
