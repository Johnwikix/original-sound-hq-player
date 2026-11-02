using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;
/// <summary>真实SQLite的十万行容量探针，输入构造不计入评分分配。</summary>
internal static class ScaleRegression
{
    public static async Task RunAsync()
    {
        const int count = 100000;
        string root = Path.Combine(AppContext.BaseDirectory,"scale-data",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new SQLiteAsyncConnection(Path.Combine(root,"scale.db"));
        AudioAnalysisService? service = null;
        try
        {
            await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY,SourceId INTEGER,Path TEXT,Title TEXT)");
            await db.ExecuteAsync("CREATE TABLE RemoteTrack(MusicId INTEGER PRIMARY KEY,SourceId INTEGER,Href TEXT,Length INTEGER,ETag TEXT,Modified TEXT,Missing INTEGER)");
            await db.ExecuteAsync("CREATE TABLE WebDavSource(Id INTEGER PRIMARY KEY,Enabled INTEGER)");
            var repo = new AnalysisRepository(db); await repo.InitializeAsync();
            var blobs = new byte[32][];
            for(int k=0;k<32;k++)
            {
                var vector = new float[384];
                for(int i=0;i<vector.Length;i++)vector[i]=MathF.Sin(k*.17f+i*.3f);
                blobs[k]=MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
            }
            string source = Path.Combine(root,"not-present.wav");
            await db.RunInTransactionAsync(tx =>
            {
                for(int i=1;i<=count;i++)
                {
                    tx.Execute("INSERT INTO Music VALUES(?,0,?,'Scale')",i,source);
                    tx.Execute("INSERT INTO AnalysisRecord(MusicId,SourceKey,Fingerprint,Version,Data,RetryAfterTicks) VALUES(?,?,'',2,?,0)",i,source,blobs[i%32]);
                }
            });
            var lifecycle = new AppLifecycle();
            service = new(new(repo),new RemoteAudioCache(),new AppViewModel(),NullLogger<AudioAnalysisService>.Instance,lifecycle);
            await service.StartAsync();
            await Task.Delay(100);
            using(var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                while(service.Progress.Phase!="Idle")await Task.Delay(30,timeout.Token);
            string[] artists=["Artist"];
            var tracks = new RecommendationTrack[count];
            for(int i=0;i<count;i++)tracks[i]=new(i+1,artists,"","","",2020,i==0,true,0,"Scale "+i);
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            var scores = await service.GetScoresAsync(tracks,[],default);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long allocated = GC.GetTotalAllocatedBytes(true)-before;
            if(scores.Count!=count || scores.Values.Any(x=>!float.IsFinite(x)))throw new Exception("Invalid streaming scale result");
            string report = string.Create(CultureInfo.InvariantCulture,$"NativeAOT={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}\r\nrows={count}\r\nscore_ms={ms:F3}\r\nmanaged_allocated_bytes={allocated}\r\nbytes_per_track={(double)allocated/count:F3}\r\n");
            Console.WriteLine(report);
            File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(),"_tools/artifacts/analysis-index-scale.txt"),report);
        }
        finally
        {
            if(service is not null)await service.StopAsync();
            await db.CloseAsync();
        }
    }
}
