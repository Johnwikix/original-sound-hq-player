using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;
if (args.Length > 0 && args[0] == "--taste-only") { await AudioTasteRegression.RunAsync(); return; }
if (args.Length > 0 && args[0] == "--scale") { await ScaleRegression.RunAsync(); return; }
if (args.Length > 0 && args[0] == "--cache-only") { await CacheLocationRegression.RunAsync(); return; }
int checks = 0;
void Check(bool condition,string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
async Task Wait(Func<bool> done,int milliseconds=15000)
{
    long start = Stopwatch.GetTimestamp();
    while (!done()) { if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > milliseconds) throw new TimeoutException(); await Task.Delay(25); }
}
string root = Path.Combine(AppContext.BaseDirectory,"test-data",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string source;
string manifestFile = "_tools/artifacts/real-music-comparison/manifest.json";
if (args.Length > 0) source = Path.GetFullPath(args[0]);
else if (File.Exists(manifestFile))
{
    using var manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
    source = manifest.RootElement.GetProperty("Tracks")[0].GetProperty("Path").GetString()!;
}
else
{
    source = Path.Combine(root,"fixture.wav");
    using var writer = new BinaryWriter(File.Create(source));
    writer.Write("RIFF"u8); writer.Write(36 + 640000); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(32000); writer.Write(64000); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(640000);
    for(int i=0;i<320000;i++) writer.Write((short)(Math.Sin(2*Math.PI*440*i/32000)*10000));
}
Console.WriteLine("Audio fixture: " + source);
await using (var client = new AnalysisWorkerClient())
{
    var data = await client.AnalyzeAsync(source, default);
    Check(data.Length==384 && data.All(float.IsFinite),"actual NativeAOT worker decodes local audio and returns finite fixed vectors");
    using var cancel = new CancellationTokenSource(60);
    try { await client.AnalyzeAsync(source,cancel.Token); throw new Exception("Cancellation ignored"); }
    catch(OperationCanceledException) { Check(true,"in-flight cancellation waits for worker exit"); }
    data = await client.AnalyzeAsync(source,default);
    Check(data.Length==384,"worker restarts after cancellation");
}
var cache = new RemoteAudioCache();
cache.Configure(true,root,10000000);
var entry = new WebDavEntry("/song", "", false,4,"v1",null);
Check(cache.AcquireCompleteRead("webdav://1/song",entry)==null && !Directory.Exists(WebDavCachePaths.Audio(root)),"analysis cache miss creates no directory or download");
Directory.CreateDirectory(WebDavCachePaths.Audio(root));
string cacheFile=Path.Combine(WebDavCachePaths.Audio(root),RemoteAudioCache.GetKey("webdav://1/song",entry)+".audio");
File.WriteAllBytes(cacheFile,[1,2,3,4]);
await using(var lease=cache.AcquireCompleteRead("webdav://1/song",entry))
{
    Check(lease?.IsCurrent==true,"complete version acquires protected read lease");
    await using var playbackLease = cache.Acquire("webdav://1/song",entry);
    Check(playbackLease?.IsComplete==true,"playback shares an existing complete analysis read lease without network fallback");
    cache.Configure(false,root,0);
    Check(lease!.IsCurrent,"lowering download budget does not invalidate an existing complete read");
    Check(cache.AcquireCompleteRead("webdav://1/song",entry with {ETag="v2"})==null,"different ETag cannot reuse old cache");
    cache.Clear();
    Check(!lease!.IsCurrent && File.Exists(cacheFile),"clear invalidates result while protecting active file");
    await Task.WhenAll(lease.DisposeAsync().AsTask(), lease.DisposeAsync().AsTask());
    Check(File.Exists(cacheFile),"repeated disposal cannot release another playback reader's cache lease");
}
Check(!File.Exists(cacheFile),"read lease release completes deferred deletion");
var db=new SQLiteAsyncConnection(Path.Combine(root,"analysis.db"));
AudioAnalysisService? service=null;
try
{
    await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,SourceId INTEGER,Path TEXT,Title TEXT)");
    await db.ExecuteAsync("CREATE TABLE RemoteTrack(MusicId INTEGER PRIMARY KEY,SourceId INTEGER,Href TEXT,Length INTEGER,ETag TEXT,Modified TEXT,Missing INTEGER)");
    await db.ExecuteAsync("CREATE TABLE WebDavSource(Id INTEGER PRIMARY KEY,Enabled INTEGER)");
    var repo=new AnalysisRepository(db); await repo.InitializeAsync();
    Check(await repo.ReadEnabledAsync(),"fresh analysis uses confirmed default");
    await repo.SaveEnabledAsync(false); Check(!await repo.ReadEnabledAsync(),"analysis switch persists");
    for(int id=1;id<=130;id++) await db.ExecuteAsync("INSERT INTO Music VALUES(?,0,?,?)",id,source,"Track "+id);
    Check((await repo.ReadSourcesAsync(0)).Count==64 && (await repo.ReadSourcesAsync(128)).Count==2,"source scanning uses keyset pagination");
    for(int id=1;id<=16;id++)
    {
        var raw=new float[384];
        for(int i=0;i<raw.Length;i++) raw[i]=MathF.Sin(i*.17f+id*.12f);
        await repo.SaveAsync(new(){MusicId=id,SourceKey=source,Fingerprint="old",Data=MemoryMarshal.AsBytes(raw.AsSpan()).ToArray()});
    }
    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            await repo.VisitVectorsAsync(0, (id, vector) => cancellation.Cancel(), cancellation.Token);
            throw new Exception("Borrowed BLOB visitor ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            Check((await repo.ReadVectorsAsync(0)).Count==16,"cancelled BLOB visitor finalizes its statement and releases the connection lock");
        }
    }
    await db.ExecuteAsync("INSERT INTO WebDavSource VALUES(1,1)");
    await db.ExecuteAsync("INSERT INTO Music VALUES(1000,1,'webdav://1/song','Remote')");
    await db.ExecuteAsync("INSERT INTO RemoteTrack VALUES(1000,1,'/song',4,'v1','',0)");
    var remote=(await repo.ReadSourcesAsync(999)).Single();
    await repo.SaveAsync(new(){MusicId=1000,SourceKey=remote.SourceKey,Data=new byte[1536]});
    Check((await repo.ReadVectorsAsync(999)).Count==1,"current remote version joins feature rows");
    await db.ExecuteAsync("UPDATE RemoteTrack SET ETag='v2' WHERE MusicId=1000");
    Check((await repo.ReadVectorsAsync(999)).Count==0,"changed remote version is excluded from recommendation scores");
    var library=new AppViewModel();
    var lifecycle = new AppLifecycle();
    service=new(new(repo),cache,library,NullLogger<AudioAnalysisService>.Instance,lifecycle);
    await service.StartAsync();
    var tracks=Enumerable.Range(1,16).Select(id=>new RecommendationTrack(id,["Artist"],"Album","Artist","Rock",2020,id==1,true,0,"Track "+id)).ToArray();
    Check((await service.GetScoresAsync(tracks,[],default)).Count==0,"disabled analysis stops consuming retained features");
    await service.SetEnabledAsync(true);
    await Wait(()=>service.Progress.Running);
    var scores=await service.GetScoresAsync(tracks,[],default);
    Check(scores.Count==16 && scores.Values.All(float.IsFinite),"streaming calibration creates finite style scores without full matrix retention");
    var unseenSeed = tracks.Select(x=>x with {IsFavorite=false}).Append(new RecommendationTrack(9999,["Artist"],"","","",2020,true,true,0,"Unanalyzed")).ToArray();
    Check((await service.GetScoresAsync(unseenSeed,[],default)).Count==0,"unanalyzed preferences do not invent an acoustic profile");
    await service.SetEnabledAsync(false);
    await Wait(()=>!service.Progress.Running);
    Check(!await repo.ReadEnabledAsync(),"disable during active scan is durable");
    await service.SetEnabledAsync(true);
    await Wait(()=>service.Progress.Running);
    lifecycle.TryBeginExit(out _);
    await Wait(()=>!service.Progress.Running);
    Check(true,"application stopping cancels the actual worker before ordered cleanup reaches the service");
    await service.StopAsync(); await service.StopAsync();
    Check(true,"repeated stop drains bounded producer, consumer and process");
}
finally
{
    if(service is not null) await service.StopAsync();
    await db.CloseAsync();
}
// Idle behavior is verified in a fresh real process; no service or timer keeps it alive.
var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"AudioAnalysisWorker.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
using(var idle=Process.Start(info)!)
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(27));
    await idle.WaitForExitAsync(deadline.Token);
    Check(idle.ExitCode==0,"idle worker exits without requiring stdin closure");
}
Console.WriteLine($"PASS {checks} integration checks");
