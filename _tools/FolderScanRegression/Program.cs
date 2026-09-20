using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using Windows.Storage;

await FolderAccessRegression.RunAsync();

string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderScanRegression-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    for (int i = 0; i < 150; i++) File.WriteAllText(System.IO.Path.Combine(root, $"{i}.mp3"), "");
    File.WriteAllText(System.IO.Path.Combine(root, "slow.mp3"), "");
    var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int count = 0;
    Func<IReadOnlyList<(WinUIMusicPlayer.Model.Music Music, string Lyrics)>, Task> consume = batch =>
    {
        count += batch.Count;
        firstBatch.TrySetResult();
        return Task.CompletedTask;
    };
    var scan = args.Contains("--legacy")
        ? LegacyScanAsync(new StorageFolder(root), consume)
        : new AddFolderService().GetMusicFilesRecursiveBatched(new StorageFolder(root), consume);
    bool progressive = await Task.WhenAny(firstBatch.Task, Task.Delay(2000)) == firstBatch.Task;
    ToolUtils.SlowFile.TrySetResult();
    await scan;
    if (!progressive) throw new Exception("FAIL: a slow file blocks all batches in the same directory.");
    if (count != 151) throw new Exception($"FAIL: expected 151 songs, got {count}.");
    Console.WriteLine("PASS: first batch arrives before slow file completes; all 151 songs delivered.");
    await RegressionSuite.RunAsync(root);
    await MetadataRegression.RunAsync(root);
}

finally
{
    ToolUtils.SlowFile.TrySetResult();
    // SQLite 异步连接句柄可能晚于断言释放（仅清理竞态,与检查结果无关）：小步重试后仍失败则留给系统临时目录清理。
    for (int attempt = 0; ; attempt++)
    {
        try { Directory.Delete(root, true); break; }
        catch (IOException) when (attempt < 10) { await Task.Delay(100); }
        catch (UnauthorizedAccessException) when (attempt < 10) { await Task.Delay(100); }
    }
}

// Captured legacy ordering: directory-wide WhenAll precedes the first batch.
static async Task LegacyScanAsync(StorageFolder folder,
    Func<IReadOnlyList<(WinUIMusicPlayer.Model.Music Music, string Lyrics)>, Task> consume)
{
    using var semaphore = new SemaphoreSlim(8);
    var tasks = (await folder.GetFilesAsync()).Select(async file =>
    {
        await semaphore.WaitAsync();
        try { return await ToolUtils.GetMusicInfo(file); }
        finally { semaphore.Release(); }
    }).ToList();
    var results = await Task.WhenAll(tasks);
    foreach (var batch in results.Chunk(100))
        await consume(batch.Select(r => (r.Item1!, r.Item2 ?? "")).ToArray());
}
