using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;

string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
Directory.CreateDirectory(fixtures);
string large = Path.Combine(fixtures, "large_raw.bin");
string portrait = Path.Combine(fixtures, "portrait_raw.bin");
string small = Path.Combine(fixtures, "small_raw.bin");
await WriteBmp(large, 3072, 3072);
await WriteBmp(portrait, 800, 2400);
await WriteBmp(small, 320, 200);

string shared = await PlaybackCoverImage.GetOrCreateAsync(large, default);
await ExpectSize(shared, 1536, 1536);
string portraitShared = await PlaybackCoverImage.GetOrCreateAsync(portrait, default);
await ExpectSize(portraitShared, 512, 1536);
Check(await PlaybackCoverImage.GetOrCreateAsync(small, default) == small, "小图应复用原文件");
await ExpectSize(small, 320, 200);
Console.WriteLine("PASS: 正方形/非正方形最长边 1536px，小图不放大");

string jpeg = Path.Combine(fixtures, "oriented_raw.bin");
using (var jpegSourceFile = File.OpenRead(portrait))
using (var jpegSource = jpegSourceFile.AsRandomAccessStream())
using (var jpegOutputFile = File.Create(jpeg))
using (var jpegOutput = jpegOutputFile.AsRandomAccessStream())
{
    var decoder = await BitmapDecoder.CreateAsync(jpegSource);
    using var bitmap = await decoder.GetSoftwareBitmapAsync();
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpegOutput);
    encoder.SetSoftwareBitmap(bitmap);
    await encoder.BitmapProperties.SetPropertiesAsync(new[]
    {
        new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation",
            new BitmapTypedValue((ushort)6, PropertyType.UInt16))
    });
    await encoder.FlushAsync();
}
await ExpectSize(jpeg, 2400, 800);
await ExpectSize(await PlaybackCoverImage.GetOrCreateAsync(jpeg, default), 1536, 512);

string png = Path.Combine(fixtures, "transparent_raw.bin");
using (var pngOutputFile = File.Create(png))
using (var pngOutput = pngOutputFile.AsRandomAccessStream())
{
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngOutput);
    byte[] pixels = new byte[2048 * 1024 * 4];
    for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 3] = 128; }
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2048, 1024, 96, 96, pixels);
    await encoder.FlushAsync();
}
string pngShared = await PlaybackCoverImage.GetOrCreateAsync(png, default);
await ExpectSize(pngShared, 1536, 768);
using (var pngFile = File.OpenRead(pngShared))
using (var pngStream = pngFile.AsRandomAccessStream())
{
    var decoder = await BitmapDecoder.CreateAsync(pngStream);
    var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
        new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    byte[] data = pixels.DetachPixelData();
    Check(data[3] == 128 && Math.Abs(data[0] - 200) <= 1, "缩放丢失 PNG 透明度或颜色");
}
Console.WriteLine("PASS: JPEG EXIF 旋转与 PNG 透明度");

await CheckDirectoryCoverAsync(fixtures, jpeg, png);
File.Delete(shared);
var merged = await Task.WhenAll(PlaybackCoverImage.GetOrCreateAsync(large, default),
    PlaybackCoverImage.GetOrCreateAsync(large, default));
Check(merged[0] == merged[1], "首次生成应共享同一文件");
long stamp = File.GetLastWriteTimeUtc(shared).Ticks;
Check(await PlaybackCoverImage.GetOrCreateAsync(large, default) == shared &&
    File.GetLastWriteTimeUtc(shared).Ticks == stamp, "热缓存不能重新编码");
Console.WriteLine("PASS: 并发首次生成及热缓存复用");

using (var mapped = ReadOnlyMappedStream.Open(shared))
using (var first = mapped.CloneStream())
using (var second = mapped.CloneStream())
{
    mapped.Dispose();
    first.Seek(10);
    Check(second.Position == 0, "克隆共享了读取位置");
    var decoder = await BitmapDecoder.CreateAsync(second);
    Check(decoder.PixelWidth == 1536, "所有者释放使克隆失效");
    using var input = first.GetInputStreamAt(0);
    using var bytes = input.AsStreamForRead();
    byte[] signature = new byte[8];
    await bytes.ReadExactlyAsync(signature);
    Check(signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "带位置的输入流未读取正确内容");
}
Console.WriteLine("PASS: 映射流克隆独立位置及所有者提前释放");

string cancelled = Path.Combine(fixtures, "cancelled_raw.bin");
File.Copy(large, cancelled, true);
string cancelledCache = PlaybackCoverImage.GetCachePath(cancelled);
File.Delete(cancelledCache);
using (var cancellation = new CancellationTokenSource())
{
    cancellation.CancelAfter(1);
    try
    {
        await PlaybackCoverImage.GetOrCreateAsync(cancelled, cancellation.Token);
        throw new Exception("取消未传播");
    }
    catch (OperationCanceledException) { }
}
Check(!File.Exists(cancelledCache) && !Directory.EnumerateFiles(fixtures, "*.part").Any(),
    "取消后不能留下缓存或临时文件");
await PlaybackCoverImage.GetOrCreateAsync(cancelled, default);
Console.WriteLine("PASS: 取消不发布半成品，并允许重试");

string invalid = Path.Combine(fixtures, "invalid_raw.bin");
await File.WriteAllTextAsync(invalid, "invalid image");
try
{
    await PlaybackCoverImage.GetOrCreateAsync(invalid, default);
    throw new Exception("损坏图片未失败");
}
catch (Exception ex) when (ex.Message != "损坏图片未失败") { }
Check(!File.Exists(PlaybackCoverImage.GetCachePath(invalid)), "损坏图片不能进入缓存");

string replaced = Path.Combine(fixtures, "replaced_raw.bin");
File.Copy(large, replaced, true);
string replacedCache = await PlaybackCoverImage.GetOrCreateAsync(replaced, default);
File.Copy(portrait, replaced, true);
File.SetLastWriteTimeUtc(replacedCache, DateTime.UtcNow.AddMinutes(-1));
await ExpectSize(await PlaybackCoverImage.GetOrCreateAsync(replaced, default), 512, 1536);
Console.WriteLine("PASS: 损坏输入及源文件更新");

using var media = new SystemMediaControlsService(NullLogger<SystemMediaControlsService>.Instance);
media.Initialize(new PlaybackCommands());
Check(media.SystemMediaControls is not null, "真实 SMTC 初始化失败");
await media.UpdateMediaInfoFromFile("高分辨率", "artist", "album", shared);
using (var transmitted = await media.SystemMediaControls.DisplayUpdater.Thumbnail.OpenReadAsync())
{
    var decoder = await BitmapDecoder.CreateAsync(transmitted);
    Check(decoder.PixelWidth == 1536 && decoder.PixelHeight == 1536, "SMTC 尺寸不符");
    using var expectedFile = File.OpenRead(shared);
    using var expected = expectedFile.AsRandomAccessStream();
    Check(transmitted.Size == expected.Size, "SMTC 未使用共享文件");
    // 比较整份编码数据，防止只检查尺寸而漏掉传错封面。
    using var actualBytes = transmitted.AsStreamForRead();
    var actualBuffer = new byte[8192];
    var expectedBuffer = new byte[8192];
    transmitted.Seek(0);
    int read;
    while ((read = await expectedFile.ReadAsync(expectedBuffer)) > 0)
    {
        await actualBytes.ReadExactlyAsync(actualBuffer.AsMemory(0, read));
        Check(expectedBuffer.AsSpan(0, read).SequenceEqual(actualBuffer.AsSpan(0, read)), "SMTC 内容不符");
    }
}
Console.WriteLine("PASS: 真实 SMTC 传出内容与共享 1536px 文件完全相同");

File.Delete(shared);
using (var retained = await media.SystemMediaControls.DisplayUpdater.Thumbnail.OpenReadAsync())
{
    var decoder = await BitmapDecoder.CreateAsync(retained);
    Check(decoder.PixelWidth == 1536, "清空缓存使当前 SMTC 封面失效");
}
await media.UpdateMediaInfoFromFile("release removed cache", "artist", "album", portraitShared);
shared = await PlaybackCoverImage.GetOrCreateAsync(large, default);
Console.WriteLine("PASS: 当前 SMTC 封面不阻止缓存删除，删除后在途读取仍可完成");

var requests = new List<Task>();
for (int i = 0; i < 40; ++i)
    requests.Add(media.UpdateMediaInfoFromFile("old-" + i, "artist", "album", shared));
requests.Add(media.UpdateMediaInfoFromFile("latest", "artist", "album", portraitShared));
await Task.WhenAll(requests);
Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == "latest", "旧更新覆盖当前歌曲");
using (var thumbnail = await media.SystemMediaControls.DisplayUpdater.Thumbnail.OpenReadAsync())
{
    var decoder = await BitmapDecoder.CreateAsync(thumbnail);
    Check(decoder.PixelWidth == 512 && decoder.PixelHeight == 1536, "切歌后传错封面");
}
await media.UpdateMediaInfoFromFile("missing cover", "artist", "album", invalid + ".missing");
Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == "missing cover", "缺失封面阻止元数据更新");
Console.WriteLine("PASS: 连续切歌只提交最新封面，缺失文件仍更新文字");

byte[] fallbackBytes = await File.ReadAllBytesAsync(small);
await media.UpdateMediaInfoFromFile("file preferred", "artist", "album", portraitShared, fallbackBytes);
await ExpectThumbnailSize(media, 512, 1536);
// 让已选定的原图在真正打开时不可用，覆盖检查与使用之间的失败。
using (var locked = new FileStream(small, FileMode.Open, FileAccess.Read, FileShare.None))
{
    await media.UpdateMediaInfoFromFile("bytes after open failure", "artist", "album", small, fallbackBytes);
    await ExpectThumbnailSize(media, 320, 200);
}
await media.UpdateMediaInfoFromFile("no file", "artist", "album", null, fallbackBytes);
await ExpectThumbnailSize(media, 320, 200);
requests.Clear();
for (int i = 0; i < 40; ++i)
    requests.Add(media.UpdateMediaInfoFromFile("cancel-fallback-" + i, "artist", "album", invalid + ".missing", fallbackBytes));
requests.Add(media.UpdateMediaInfoFromFile("latest file", "artist", "album", portraitShared));
await Task.WhenAll(requests);
Check(media.SystemMediaControls.DisplayUpdater.MusicProperties.Title == "latest file", "取消的字节兜底覆盖当前歌曲");
await ExpectThumbnailSize(media, 512, 1536);
Console.WriteLine("PASS: 原图优先、打开失败转字节、无文件字节兜底及兜底取消");

// 两组使用同一分辨率/编码文件和真实 SMTC；不包含测试图生成与首次编码。
await media.UpdateMediaInfoFromFile("warm", "artist", "album", shared);
await Measure("同尺寸字节数组提交", async () =>
{
    byte[] bytes = await File.ReadAllBytesAsync(shared);
    await media.UpdateMediaInfo("array", "artist", "album", bytes);
});
await Measure("共享热缓存文件流提交", async () =>
{
    string path = await PlaybackCoverImage.GetOrCreateAsync(large, default);
    await media.UpdateMediaInfoFromFile("stream", "artist", "album", path);
});

requests.Clear();
for (int i = 0; i < 40; ++i)
    requests.Add(media.UpdateMediaInfoFromFile("shutdown-" + i, "artist", "album",
        i % 2 == 0 ? shared : invalid + ".missing", fallbackBytes));
await media.StopAsync();
Check(requests.All(task => task.IsCompleted), "退出未等待全部旧更新");
await Task.WhenAll(requests);
media.Dispose();
foreach (string path in new[] { shared, portraitShared })
{
    using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}
Console.WriteLine("PASS: 退出排空全部更新并释放封面文件句柄");

static async Task ExpectThumbnailSize(SystemMediaControlsService media, uint width, uint height)
{
    using var thumbnail = await media.SystemMediaControls.DisplayUpdater.Thumbnail.OpenReadAsync();
    var decoder = await BitmapDecoder.CreateAsync(thumbnail);
    Check(decoder.OrientedPixelWidth == width && decoder.OrientedPixelHeight == height,
        "SMTC 兜底封面内容不符");
}

static async Task ExpectSize(string path, uint width, uint height)
{
    using var file = File.OpenRead(path);
    using var stream = file.AsRandomAccessStream();
    var decoder = await BitmapDecoder.CreateAsync(stream);
    Check(decoder.OrientedPixelWidth == width && decoder.OrientedPixelHeight == height,
        $"图片尺寸 {decoder.OrientedPixelWidth}×{decoder.OrientedPixelHeight}，期望 {width}×{height}");
}

static async Task WriteBmp(string path, int width, int height)
{
    await using var file = File.Create(path);
    byte[] header = new byte[54];
    header[0] = (byte)'B'; header[1] = (byte)'M';
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 54 + width * height * 4);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 54);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), -height);
    BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(26), 1);
    BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(28), 32);
    await file.WriteAsync(header);
    byte[] row = new byte[width * 4];
    var random = new Random(42);
    for (int y = 0; y < height; ++y)
    {
        random.NextBytes(row);
        for (int x = 3; x < row.Length; x += 4) row[x] = 255;
        await file.WriteAsync(row);
    }
}

static async Task Measure(string name, Func<Task> operation)
{
    await operation();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
    long before = GC.GetTotalAllocatedBytes(true);
    var elapsed = Stopwatch.StartNew();
    const int iterations = 10;
    for (int i = 0; i < iterations; ++i) await operation();
    elapsed.Stop();
    long allocated = GC.GetTotalAllocatedBytes(true) - before;
    Console.WriteLine($"MEASURE: {name}: {allocated / iterations:N0} B/update; " +
        $"{elapsed.Elapsed.TotalMilliseconds / iterations:F1} ms/update; " +
        $"10 updates GC={GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task CheckDirectoryCoverAsync(string fixtures, string jpeg, string png)
{
    string dir = Path.Combine(fixtures, "cue-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var music = new WinUIMusicPlayer.Model.Music { Path = Path.Combine(dir, "image.flac") };
        string cover = Path.Combine(dir, "cover.jpg");
        File.Copy(jpeg, Path.Combine(dir, "back.jpg"));
        Check(WinUIMusicPlayer.Utils.LocalCoverSource.GetRevision(dir, true, default) == "", "Back cover excluded");
        File.Copy(jpeg, cover);
        File.Copy(png, Path.Combine(dir, "folder.png"));
        string stamp = WinUIMusicPlayer.Utils.LocalCoverSource.GetRevision(dir, true, default);
        int opens = 0;
        byte[] Embedded(WinUIMusicPlayer.Model.Music _) { Interlocked.Increment(ref opens); return []; }
        var reads = await Task.WhenAll(Enumerable.Range(0, 9).Select(_ =>
            WinUIMusicPlayer.Utils.LocalCoverSource.ReadAsync(music, stamp, Embedded, default)));
        Check(opens == 1 && reads.All(b => b.AsSpan().SequenceEqual(File.ReadAllBytes(jpeg))), "Nine CUE tracks share one read of the front cover");
        var time = File.GetLastWriteTimeUtc(cover);
        File.WriteAllBytes(cover, new byte[new FileInfo(cover).Length]);
        File.SetLastWriteTimeUtc(cover, time);
        string replaced = WinUIMusicPlayer.Utils.LocalCoverSource.GetRevision(dir, true, default);
        Check(replaced != stamp, "Equal-size replacement with preserved timestamp invalidates artwork");
        var fallback = await WinUIMusicPlayer.Utils.LocalCoverSource.ReadAsync(music, replaced, Embedded, default);
        Check(fallback.AsSpan().SequenceEqual(File.ReadAllBytes(png)), "Damaged front cover falls back to folder.png");
    }
    finally { Directory.Delete(dir, true); }
}
