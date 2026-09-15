using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

internal static class MetadataRegression
{
    public static async Task RunAsync(string root)
    {
        string dbPath = Path.Combine(root, "metadata.db");
        string filePath = Path.Combine(root, "metadata.mp3");
        await File.WriteAllTextAsync(filePath, "original");
        var db = new MusicDatabaseService(dbPath);
        await db.InitializeAsync();
        var music = new Music { Path = filePath, Title = "first" };
        await db.Connection.InsertAsync(music);
        var cover = new byte[] { 1, 2, 3 };
        await db.QueueMetadataWriteAsync(music, cover, "lyrics", "krc", "translation", "tkrc");
        cover[0] = 9;
        Check((await db.Connection.FindAsync<PendingMetadataWrite>(filePath)).Cover![0] == 1, "cover snapshot");
        using (var held = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await db.FlushMetadataWritesAsync(default);
            Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 1, "locked write retained");
            Check(NotificationService.Count == 0, "sharing violation is quiet");
            music.Title = "latest";
            await db.QueueMetadataWriteAsync(music, null, "new lyrics", null, null, null);
            var batch = new List<(Music Music, string Lyrics)> { (new Music { Id = music.Id, Path = filePath, Title = "stale" }, "old") };
            var commit = typeof(MusicDatabaseService).GetMethod("CommitScanBatchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)commit.Invoke(db, [batch, null])!;
            Check((await db.Connection.FindAsync<Music>(music.Id)).Title == "latest", "scan preserves pending edits");
        }
        await db.Connection.CloseAsync();
        db = new MusicDatabaseService(dbPath);
        // Reopen the persisted queue without recreating test indexes.
        await db.FlushMetadataWritesAsync(default);
        Check(await File.ReadAllTextAsync(filePath) == "latest", "restart applies latest snapshot");
        Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 0, "success removes request");
        await db.QueueMetadataWriteAsync(music, null, null, null, null, null);
        var writer = ToolUtils.WriteMetadata;
        ToolUtils.WriteMetadata = (_, _) => throw new IOException("unsupported tag write");
        try
        {
            await db.FlushMetadataWritesAsync(default);
            await db.FlushMetadataWritesAsync(default);
            Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 1, "failure retained");
            Check(NotificationService.Count == 1, "failure reported once");
        }
        finally { ToolUtils.WriteMetadata = writer; }
        await db.FlushMetadataWritesAsync(default);
        Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 0, "failed write can recover");
        await db.Connection.CloseAsync();
        Console.WriteLine("PASS: metadata lock/release, snapshot, replacement, restart, scan protection and failure recovery.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
