using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.LastFm;
using WinUIMusicPlayer.Services.Recommendations;
int checks=0;
void Check(bool value,string message) { if(!value) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
var credentials=new LastFmCredentials(new('a',32),new('b',32));
var handler=new Handler();
using var api=new LastFmApiClient(new HttpClient(handler),new Uri("https://fixture.invalid/"));
var artists=await api.SimilarAsync(credentials,"Artist",null,default);
Check(artists.Count==40 && artists.All(x=>x.Match>=0 && x.Match<=1),"artist array is bounded and numeric match is validated");
var tracks=await api.SimilarAsync(credentials,"Artist","Seed",default);
Check(tracks.Count==1 && tracks[0].Artist=="Other" && tracks[0].Title=="Song" && tracks[0].Match<1,"single track object parses with conservative artist/title identity");
string root=Path.Combine(AppContext.BaseDirectory,"data",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var store=new LastFmStore(new Vault(credentials),Path.Combine(root,"state.json"));
await store.SaveAsync(new(){Enabled=true,HasCredentials=true},default);
var account=new LastFmService(api,store);await account.StartAsync();
try
{
    handler.Hold=true;
    var request=account.GetSimilarAsync("Artist",null,account.Status.Revision,default);
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var nowPlaying=account.NowPlayingAsync(new("Song","Artist","",180),account.Status.Revision,default);
    await Task.Delay(30);Check(!nowPlaying.IsCompleted,"similarity uses the same serialized network slot as playback reporting");
    await account.SetEnabledAsync(false);
    try {await request;throw new Exception("late request accepted");}catch(OperationCanceledException){Check(true,"disable cancels in-flight similarity request");}
    await nowPlaying;
    handler.Hold=false;
    await account.SetEnabledAsync(true);
    var enrichment=new LastFmRecommendationService(account,new MusicDatabaseService(),NullLogger<LastFmRecommendationService>.Instance);
    enrichment.Start();enrichment.Start();
    try
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while(enrichment.Snapshot.Tracks.Count==0)await Task.Delay(20,deadline.Token);
        Check(enrichment.Snapshot.Artists.Count==40 && enrichment.Snapshot.Tracks.ContainsKey(CommunitySignals.TrackKey("Other","Song")),"background cache publishes bounded community signals");
        Check(handler.InvalidRequests==2,"unlisted artist and track seeds do not block valid community results");
        await account.SetEnabledAsync(false);
        Check(enrichment.Snapshot.Artists.Count==0 && enrichment.Snapshot.Tracks.Count==0,"disabled account immediately clears recommendation influence");
    }
    finally {await enrichment.StopAsync();await enrichment.StopAsync();}
    Check(true,"enrichment stop is idempotent and drains requests");
}
finally {await account.StopAsync();}
Console.WriteLine($"PASS {checks} Last.fm enhancement checks");
sealed class Vault(LastFmCredentials credentials):ILastFmCredentialVault
{
    private string? _value=JsonSerializer.Serialize(new LastFmIdentity(credentials,new("alice","session")),LastFmJsonContext.Default.LastFmIdentity);
    public Task<string?> ReadAsync(CancellationToken token)=>Task.FromResult(_value);
    public Task WriteAsync(string? value,CancellationToken token){_value=value;return Task.CompletedTask;}
}
sealed class Handler:HttpMessageHandler
{
    public bool Hold;
    public int InvalidRequests;
    public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var values=(await request.Content!.ReadAsStringAsync(token)).Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>WebUtility.UrlDecode(x[0]),x=>WebUtility.UrlDecode(x[1]));
        string method=values["method"];
        if(method.Contains("getSimilar"))
        {
            if(values["limit"]!="40" || values["autocorrect"]!="0")throw new Exception("unbounded API query");
            if(Hold){Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
        }
        if(method.Contains("getSimilar") && values["artist"]=="Unknown Seed")
        {
            Interlocked.Increment(ref InvalidRequests);
            return new(HttpStatusCode.OK){Content=new StringContent("{\"error\":6}")};
        }
        string json=method switch
        {
            "artist.getSimilar"=>JsonSerializer.Serialize(new {similarartists=new {artist=Enumerable.Range(0,80).Select(i=>new{name="Artist "+i,match="0.9"})}}),
            "track.getSimilar"=>"{\"similartracks\":{\"track\":{\"name\":\"Song\",\"match\":10.95,\"artist\":{\"name\":\"Other\"}}}}",
            "track.updateNowPlaying"=>"{\"nowplaying\":{}}",
            _=>throw new Exception("Unexpected request "+method)
        };
        return new(HttpStatusCode.OK){Content=new StringContent(json)};
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService
    {
        public Task<List<Seed>> GetRecommendationSeedsAsync()=>Task.FromResult(new List<Seed>{new(){Artist="Unknown Seed",Title="Missing"},new()});
        public sealed class Seed{public string Artist{get;set;}="Artist";public string Title{get;set;}="Seed";}
    }
}
