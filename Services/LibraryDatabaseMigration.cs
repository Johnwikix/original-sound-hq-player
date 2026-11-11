using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>曲库的唯一迁移入口。新增大规模结构或数据改写时递增 CurrentVersion。</summary>
internal static class LibraryDatabaseMigration
{
    internal const int CurrentVersion = 2;
    internal const string ActiveFile = "MusicDatabase.song-v1.db";
    internal const string LegacyFile = "MusicDatabase.db";
    private const string CheckpointTable = "LibraryMigrationCheckpoint";

    internal static async Task<SQLiteAsyncConnection> OpenAsync(string? documentsDirectory, string localDirectory,
        Func<SQLiteAsyncConnection, bool, Task> initializeSchema, Action<string> backupReady)
    {
        // 启动方在 UI 线程。定位、SQLite 在线备份和完整性检查全部在工作线程完成。
        var prepared = await Task.Run(() => Prepare(documentsDirectory, localDirectory, backupReady)).ConfigureAwait(false);
        var database = new SQLiteAsyncConnection(prepared.Path);
        try
        {
            await initializeSchema(database, prepared.Upgrade).ConfigureAwait(false);
            if (prepared.Upgrade)
            {
                await database.RunInTransactionAsync(db =>
                {
                    CheckIntegrity(db);
                    db.Execute($"PRAGMA main.user_version={CurrentVersion}");
                    if (HasTable(db, CheckpointTable)) db.Execute("DELETE FROM LibraryMigrationCheckpoint");
                }).ConfigureAwait(false);
            }
            return database;
        }
        catch
        {
            await database.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static (string Path, bool Upgrade) Prepare(string? documentsDirectory, string localDirectory, Action<string> backupReady)
    {
        var (active, source) = LocateDatabase(documentsDirectory, localDirectory);
        if (source is null) return (active, true);
        bool importing = !string.Equals(source, active, StringComparison.OrdinalIgnoreCase);
        // 打开已有活动库时允许 SQLite 回滚上次崩溃留下的热日志。旧库始终只读。
        using var original = new SQLiteConnection(source, importing ? SQLiteOpenFlags.ReadOnly : SQLiteOpenFlags.ReadWrite);
        int previous = original.ExecuteScalar<int>("PRAGMA main.user_version");
        if (previous < 0 || previous > CurrentVersion ||
            (HasTable(original, "SongVersionSchema") &&
             original.ExecuteScalar<int>("SELECT Version FROM SongVersionSchema WHERE Id=1") > SongVersionSchemaRecord.CurrentVersion))
            throw new InvalidDataException($"此曲库需要更新版本的播放器：{source}");
        bool upgrade = previous < CurrentVersion;
        if (!upgrade && !importing) return (active, false);

        if (original.ExecuteScalar<int>("PRAGMA main.page_count") == 0)
        {
            if (!importing && (Exists(Path.Combine(Path.GetDirectoryName(active)!, LegacyFile)) ||
                Exists(Path.Combine(localDirectory, LegacyFile))))
                throw new InvalidDataException($"活动曲库为空，旧曲库仍在。请先移走空库再重试：{active}");
            return (active, true);
        }

        string backupDirectory = Path.Combine(Path.GetDirectoryName(active)!, "Backups");
        string? checkpoint = HasTable(original, CheckpointTable)
            ? original.ExecuteScalar<string>("SELECT Sha256 FROM LibraryMigrationCheckpoint WHERE TargetVersion=?", CurrentVersion)
            : null;
        string prefix = Path.Combine(backupDirectory, $"MusicDatabase.v{previous}-to-v{CurrentVersion}.");
        string backup;
        string digest;
        try
        {
            if (checkpoint is null)
            {
                Directory.CreateDirectory(backupDirectory);
                digest = CreateSnapshot(original, prefix, previous);
            }
            else
            {
                // 校验实际字节，不能把同名但已被替换的有效 SQLite 文件当作原始恢复点。
                if (checkpoint.Length != 64 || !checkpoint.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("曲库中的备份校验值无效。");
                digest = checkpoint;
                VerifyDigest(prefix + digest + ".db", digest);
            }
            backup = prefix + digest + ".db";
        }
        catch (Exception ex)
        {
            throw new IOException($"无法创建或验证迁移前备份，曲库迁移已停止。\n{backupDirectory}\n{ex.Message}", ex);
        }
        backupReady(backup);

        if (importing)
        {
            // 只复制已经关闭并校验过的独立快照；旧程序仍可使用原路径。
            string temporary = active + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(backup, temporary);
                File.Move(temporary, active);
            }
            finally { File.Delete(temporary); }
        }
        if (upgrade && checkpoint is null)
        {
            using var writable = new SQLiteConnection(active, SQLiteOpenFlags.ReadWrite);
            writable.RunInTransaction(() =>
            {
                writable.Execute("CREATE TABLE IF NOT EXISTS LibraryMigrationCheckpoint(TargetVersion INTEGER PRIMARY KEY, Sha256 TEXT NOT NULL)");
                writable.Execute("INSERT INTO LibraryMigrationCheckpoint(TargetVersion,Sha256) VALUES(?,?)", CurrentVersion, digest);
            });
        }
        return (active, upgrade);
    }

    private static (string Active, string? Source) LocateDatabase(string? documentsDirectory, string localDirectory)
    {
        string directory = documentsDirectory ?? localDirectory;
        string active = Path.Combine(directory, ActiveFile);
        if (Exists(active)) return (active, active);
        string localActive = Path.Combine(localDirectory, ActiveFile);
        if (Exists(localActive)) return (localActive, localActive);

        string legacy = Path.Combine(directory, LegacyFile);
        if (!Exists(legacy)) legacy = Path.Combine(localDirectory, LegacyFile);
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (directory != localDirectory && (ex is IOException or UnauthorizedAccessException))
        {
            active = localActive;
        }
        if (Exists(active + "-wal") || Exists(active + "-shm") || Exists(active + "-journal"))
            throw new IOException($"曲库主文件缺失，请先恢复数据库：{active}");
        return (active, Exists(legacy) ? legacy : null);
    }

    private static string CreateSnapshot(SQLiteConnection source, string prefix, int version)
    {
        string temporary = prefix + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 固定读事务，页数与在线备份取自同一已提交视图（包括 WAL）。
            int pages = 0;
            source.RunInTransaction(() =>
            {
                pages = source.ExecuteScalar<int>("PRAGMA main.page_count");
                source.Backup(temporary);
            });
            using (var copy = new SQLiteConnection(temporary, SQLiteOpenFlags.ReadWrite))
            {
                if (copy.ExecuteScalar<string>("PRAGMA main.journal_mode=DELETE") != "delete" ||
                    copy.ExecuteScalar<int>("PRAGMA main.page_count") != pages)
                    throw new InvalidDataException("备份未生成完整的独立数据库。");
                VerifySnapshot(copy, version);
            }
            string digest = FileDigest(temporary);
            string destination = prefix + digest + ".db";
            if (Exists(destination)) VerifyDigest(destination, digest);
            else File.Move(temporary, destination);
            return digest;
        }
        finally { File.Delete(temporary); }
    }

    private static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyDigest(string path, string expected)
    {
        if (!string.Equals(FileDigest(path), expected, StringComparison.Ordinal))
            throw new InvalidDataException($"备份 SHA-256 不匹配：{path}");
    }

    private static void VerifySnapshot(SQLiteConnection db, int version)
    {
        if (new FileInfo(db.DatabasePath).Length == 0 || db.ExecuteScalar<int>("PRAGMA main.user_version") != version)
            throw new InvalidDataException("备份为空或版本不匹配。");
        CheckIntegrity(db);
    }

    private static void CheckIntegrity(SQLiteConnection db)
    {
        string result = db.ExecuteScalar<string>("PRAGMA main.quick_check(1)");
        if (result != "ok") throw new InvalidDataException($"曲库完整性检查失败：{result}");
    }

    private static bool HasTable(SQLiteConnection db, string name) =>
        db.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=?)", name) != 0;

    // File.Exists 将拒绝访问也当作不存在，此处不能因此改选旧库或创建空库。
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
