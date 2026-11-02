using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SQLitePCL;
using System.Threading.Tasks;
namespace WinUIMusicPlayer.Services.AudioAnalysis;
/// <summary>持久化分析开关，默认启用低占空比本地分析。</summary>
public sealed class AnalysisSettings
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}
/// <summary>按文件版本保存原始片段；失败按版本退避，不反复忙重试。</summary>
public sealed class AnalysisRecord
{
    [PrimaryKey] public int MusicId { get; set; }
    public string SourceKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Version { get; set; } = 2;
    public byte[] Data { get; set; } = [];
    public long RetryAfterTicks { get; set; }
}
/// <summary>后台分页读取的值快照，不持有UI曲目。</summary>
public sealed class AnalysisSource
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Href { get; set; } = "";
    public long Length { get; set; }
    public string ETag { get; set; } = "";
    public string Modified { get; set; } = "";
    public int StatusVersion { get; set; }
    public string StatusFingerprint { get; set; } = "";
    public int StatusDataLength { get; set; }
    public long StatusRetryTicks { get; set; }
    public string SourceKey => SourceId == 0 ? Path : string.Join("\u001f", SourceId, Href, ETag, Modified, Length);
}
/// <summary>同步消费SQLite借用的特征视图，返回前不得保留span。</summary>
public delegate void VisitAnalysisVector(int musicId, ReadOnlySpan<float> vector);

/// <summary>按64行分页；连接由主程序数据库服务拥有。</summary>
public sealed class AnalysisRepository(SQLiteAsyncConnection connection, Func<string>? cacheRoot = null)
{
    private string? _attachedPath;
    private int _revision;
    /// <summary>缓存切换代次；流式评分跨目录时丢弃混合结果。</summary>
    public int CacheRevision => Volatile.Read(ref _revision);
    /// <summary>获取当前偏好位置快照。</summary>
    public string? CacheRootPath => cacheRoot?.Invoke();
    /// <summary>在读取多遍向量前打开当前缓存。</summary>
    public Task PrepareCacheAsync() => WithCache(_ => 0);
    private string Table => cacheRoot is null ? "main.AnalysisRecord" : "features.AnalysisRecord";
    /// <summary>缓存位置变化后按需切换连接；连接锁同时保护 ATTACH 和借用 BLOB 的生命周期。</summary>
    private void EnsureCache(SQLiteConnection database)
    {
        if (cacheRoot is null) return;
        string path = Path.GetFullPath(Path.Combine(cacheRoot(), "Analysis", "Features.db"));
        if (string.Equals(_attachedPath, path, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (_attachedPath is not null)
        {
            database.Execute("DETACH DATABASE features");
            _attachedPath = null;
        }
        database.Execute("ATTACH DATABASE ? AS features", path);
        try
        {
            database.Execute("CREATE TABLE IF NOT EXISTS features.AnalysisRecord (MusicId INTEGER PRIMARY KEY,SourceKey TEXT NOT NULL,Fingerprint TEXT NOT NULL,Version INTEGER NOT NULL,Data BLOB NOT NULL,RetryAfterTicks INTEGER NOT NULL)");
            if (database.ExecuteScalar<int>("SELECT count(*) FROM main.sqlite_master WHERE type='table' AND name='AnalysisRecord'") != 0)
            {
                // SQLite 内部流式复制；完成提交后才移除旧缓存，持久分析开关留在主数据库。
                database.Execute("INSERT OR IGNORE INTO features.AnalysisRecord SELECT MusicId,SourceKey,Fingerprint,Version,Data,RetryAfterTicks FROM main.AnalysisRecord");
                database.Execute("DROP TABLE main.AnalysisRecord");
            }
            _attachedPath = path;
            Interlocked.Increment(ref _revision);
        }
        catch
        {
            database.Execute("DETACH DATABASE features");
            throw;
        }
    }
    private Task<T> WithCache<T>(Func<SQLiteConnection, T> operation) => Task.Run(() =>
    {
        var database = connection.GetConnection();
        using (database.Lock())
        {
            EnsureCache(database);
            return operation(database);
        }
    });
    /// <summary>初始化持久设置；缓存延迟到偏好恢复完毕后首次使用时打开。</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AnalysisSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AnalysisRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AnalysisSource))]
    public async Task InitializeAsync()
    {
        await connection.CreateTableAsync<AnalysisSettings>().ConfigureAwait(false);
        if (cacheRoot is null) await connection.CreateTableAsync<AnalysisRecord>().ConfigureAwait(false);
    }
    /// <summary>读取持久开关。</summary>
    public async Task<bool> ReadEnabledAsync() => (await connection.FindAsync<AnalysisSettings>(1).ConfigureAwait(false))?.Enabled ?? true;
    /// <summary>保存开关。</summary>
    public Task SaveEnabledAsync(bool enabled) => connection.InsertOrReplaceAsync(new AnalysisSettings { Enabled = enabled }, typeof(AnalysisSettings));
    /// <summary>只列出本地及未删除、已启用的WebDAV元数据，不访问网络。</summary>
    public Task<List<AnalysisSource>> ReadSourcesAsync(int after) => WithCache(db => db.Query<AnalysisSource>(
        SourceSelectSql + " AND m.Id>? ORDER BY m.Id LIMIT 64", after));

    /// <summary>提交前只复核当前曲目，不额外复制后续63条记录。</summary>
    public Task<AnalysisSource?> FindSourceAsync(int id) => WithCache<AnalysisSource?>(db => db.FindWithQuery<AnalysisSource>(
        SourceSelectSql + " AND m.Id=? LIMIT 1", id));

    private string SourceSelectSql => cacheRoot is null ? SourcesMain : SourcesCache;
    private static readonly string SourcesCache = SourcesMain.Replace("main.AnalysisRecord", "features.AnalysisRecord", StringComparison.Ordinal);
    private const string SourcesMain =
        "SELECT m.Id,m.SourceId,m.Path,m.Title,r.Href,r.Length,r.ETag,r.Modified,a.Version AS StatusVersion," +
        "a.Fingerprint AS StatusFingerprint,length(a.Data) AS StatusDataLength,a.RetryAfterTicks AS StatusRetryTicks " +
        "FROM Music m LEFT JOIN RemoteTrack r ON r.MusicId=m.Id LEFT JOIN main.AnalysisRecord a ON a.MusicId=m.Id " +
        "WHERE (m.SourceId=0 OR (r.Missing=0 AND EXISTS(SELECT 1 FROM WebDavSource s WHERE s.Id=m.SourceId AND s.Enabled=1)))";

    /// <summary>按曲目查询已提交版本。</summary>
    public Task<AnalysisRecord?> FindAsync(int id) => WithCache<AnalysisRecord?>(db => db.FindWithQuery<AnalysisRecord>("SELECT * FROM " + Table + " WHERE MusicId=?", id));
    /// <summary>原子提交一个原始向量。</summary>
    public Task SaveAsync(AnalysisRecord row) => WithCache(db => db.Execute(
        "INSERT OR REPLACE INTO " + Table + " (MusicId,SourceKey,Fingerprint,Version,Data,RetryAfterTicks) VALUES(?,?,?,?,?,?)",
        row.MusicId,row.SourceKey,row.Fingerprint,row.Version,row.Data,row.RetryAfterTicks));
    /// <summary>分页读取可见分析行；流式校准不保留全库向量矩阵。</summary>
    public Task<List<AnalysisRecord>> ReadVectorsAsync(int after) => WithCache(db => db.Query<AnalysisRecord>(VectorPageSql, after));

    private string VectorPageSql => cacheRoot is null ? VectorsMain : VectorsCache;
    private static readonly string VectorsCache = VectorsMain.Replace("main.AnalysisRecord", "features.AnalysisRecord", StringComparison.Ordinal);
    private const string VectorsMain =
        "SELECT a.MusicId,a.Data FROM main.AnalysisRecord a JOIN Music m ON m.Id=a.MusicId LEFT JOIN RemoteTrack r ON r.MusicId=m.Id " +
        "WHERE a.MusicId>? AND a.Version=2 AND length(a.Data)=1536 AND ((m.SourceId=0 AND a.SourceKey=m.Path) OR " +
        "(r.Missing=0 AND EXISTS(SELECT 1 FROM WebDavSource s WHERE s.Id=m.SourceId AND s.Enabled=1) AND a.SourceKey=" +
        "CAST(m.SourceId AS TEXT)||char(31)||r.Href||char(31)||r.ETag||char(31)||r.Modified||char(31)||CAST(r.Length AS TEXT))) ORDER BY a.MusicId LIMIT 64";

    /// <summary>在SQLite连接锁下同步借用BLOB，避免每遍按曲目复制1536 B托管数组。</summary>
    /// <remarks>回调在后台线程执行，span只在当前step期间有效；不能传出回调或跨await。每页结束释放statement和锁。</remarks>
    public Task<int> VisitVectorsAsync(int after, VisitAnalysisVector visitor, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var database = connection.GetConnection();
        using (database.Lock())
        {
            EnsureCache(database);
            int prepared = raw.sqlite3_prepare_v2(database.Handle, VectorPageSql, out var statement);
            try
            {
                Check(prepared);
                Check(raw.sqlite3_bind_int(statement, 1, after));
                int last = after;
                int result;
                while ((result = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
                {
                    token.ThrowIfCancellationRequested();
                    last = raw.sqlite3_column_int(statement, 0);
                    ReadOnlySpan<byte> blob = raw.sqlite3_column_blob(statement, 1);
                    if (blob.Length == 1536) visitor(last, MemoryMarshal.Cast<byte, float>(blob));
                }
                if (result != raw.SQLITE_DONE) Check(result);
                return last;
            }
            finally { if (statement is not null) raw.sqlite3_finalize(statement); }
        }
    }, token);

    private static void Check(int result)
    {
        if (result != raw.SQLITE_OK) throw new IOException("Feature database operation failed: " + result);
    }

    /// <summary>删除已离库的分析行。</summary>
    public Task PruneAsync() => WithCache(db => db.Execute("DELETE FROM " + Table + " WHERE MusicId NOT IN (SELECT Id FROM Music)"));
}
