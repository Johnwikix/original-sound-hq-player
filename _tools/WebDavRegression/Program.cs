using FFmpeg.AutoGen;
using WinUIMusicPlayer.Services.WebDav;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;

ffmpeg.RootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Libraries/FFmpeg/x64"));
using var transport = new WebDavTransport();
if (args.Contains("--metadata-only"))
{
    await MetadataRegression.RunAsync(transport);
    return;
}
if (args.Contains("--tree-only"))
{
    await ConnectionViewModelRegression.RunTreeAsync(transport);
    return;
}
if (args.Contains("--connection-only"))
{
    await ConnectionProbe.RunAsync(transport);
    return;
}
if (!args.Contains("--integration-only"))
{
    await MetadataRegression.RunAsync(transport);
    await Regression.RunAsync(transport);
    await ConnectionViewModelRegression.RunAsync(transport);
}
ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
string? nativePlayer = Environment.GetEnvironmentVariable("MUSIC_WEBDAV_PLAYER");
if (nativePlayer is not null)
    Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", "webdav-" + Guid.NewGuid().ToString("N"));
var address = Environment.GetEnvironmentVariable("MUSIC_WEBDAV_URL");
if (address is null) { Console.WriteLine("Set MUSIC_WEBDAV_URL, MUSIC_WEBDAV_USER and MUSIC_WEBDAV_PASSWORD to run integration checks."); return; }
var connection = new WebDavConnection(WebDavTransport.NormalizeRoot(address),
    Environment.GetEnvironmentVariable("MUSIC_WEBDAV_USER") ?? "", Environment.GetEnvironmentVariable("MUSIC_WEBDAV_PASSWORD") ?? "");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var entries = new List<WebDavEntry>();
await foreach (var entry in transport.ListAsync(connection, connection.Root.AbsolutePath, deadline.Token)) entries.Add(entry);
Console.WriteLine($"PROPFIND: {entries.Count} entries.");
foreach (var entry in entries.Where(e => !e.IsDirectory).Take(2).Concat(entries.Where(e => e.Name == "STARSET-INFECTED.flac")))
{
    using var input = new HttpRangeReadStream(transport, connection, entry, deadline.Token);
    var metadata = await Task.Run(() => RemoteMetadataProbe.ReadMetadata(input, Path.GetExtension(entry.Name), deadline.Token));
    Console.WriteLine($"Metadata: {metadata.Title}; {metadata.SampleRate} Hz; {metadata.DurationMs:0} ms; {input.DownloadedBytes} bytes read.");
    using var coverInput = new HttpRangeReadStream(transport, connection, entry, deadline.Token, 8 * 1024 * 1024, 64, 20);
    var cover = await Task.Run(() => WinUIMusicPlayer.Reader.AudioCoverReader.ReadCover(coverInput, Path.GetExtension(entry.Name)));
    Console.WriteLine($"Custom cover: {cover.Length} bytes, {coverInput.DownloadedBytes} transferred.");
    var cache = new RemoteAudioCache();
    await using var bridge = new WebDavPlaybackBridge(new RemoteReadSession(transport, connection, entry, cache, entry.Href));
    bridge.Start();
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, bridge.Location);
    request.Headers.Range = new RangeHeaderValue(0, 131071);
    using var response = await http.SendAsync(request, deadline.Token);
    var received = await response.Content.ReadAsByteArrayAsync(deadline.Token);
    input.Position = 0;
    var expected = new byte[received.Length];
    input.ReadExactly(expected);
    if (!received.AsSpan().SequenceEqual(expected)) throw new Exception("OpenList bridge bytes differ");
    Console.WriteLine("PASS: OpenList bridge serves exact original audio bytes.");
    if (nativePlayer is not null)
        await NativePlaybackProbe.RunAsync(nativePlayer, bridge.Location, entry, deadline.Token);
}
if (args.Contains("--scan-all"))
{
    var queue = new Queue<string>();
    queue.Enqueue(connection.Root.AbsolutePath);
    int complete = 0, failed = 0;
    long bytes = 0;
    long allocated = GC.GetTotalAllocatedBytes(true);
    var pause = GC.GetTotalPauseDuration();
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    var clock = Stopwatch.StartNew();
    while (queue.TryDequeue(out var directory))
        await foreach (var entry in transport.ListAsync(connection, directory, deadline.Token))
        {
            if (entry.IsDirectory) { queue.Enqueue(entry.Href); continue; }
            string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not (".flac" or ".mp3" or ".m4a" or ".wav" or ".ogg" or ".opus" or ".ape" or ".wv" or ".dsf" or ".dff" or ".aac" or ".wma" or ".aiff")) continue;
            using var input = new HttpRangeReadStream(transport, connection, entry, deadline.Token);
            try
            {
                var result = await Task.Run(() => RemoteMetadataProbe.ReadMetadata(input, extension, deadline.Token));
                if (result.SampleRate <= 0) throw new Exception("missing sample rate");
                complete++;
            }
            catch (Exception ex) { failed++; Console.WriteLine($"Metadata deferred: {entry.Name}, {(ex is WebDavException dav ? dav.Code : ex.GetType().Name)}"); }
            bytes += input.DownloadedBytes;
        }
    Console.WriteLine($"Full metadata pass: complete={complete}, deferred={failed}, elapsed={clock.Elapsed.TotalSeconds:F2}s, audioBytes={bytes}, managedAllocated={GC.GetTotalAllocatedBytes(true)-allocated}, collections={GC.CollectionCount(0)-g0}/{GC.CollectionCount(1)-g1}/{GC.CollectionCount(2)-g2}, runtimeReportedGcPauseMs={(GC.GetTotalPauseDuration()-pause).TotalMilliseconds:F2}");
    if (failed != 0) throw new Exception("Some metadata probes deferred.");
}
