using BassPlayerIpc.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length < 1) throw new ArgumentException("Pass an AudioPlayer.exe directory containing FFmpeg DLLs.");
var ring = new AudioPlayer.Playback.PcmRing(1, 16, 4, -1, resumeFrames: 8);
double[] samples = Enumerable.Repeat(0.1, 8).ToArray();
double[] rendered = new double[8];
ring.Push(samples, 2, () => false);
Check(ring.Render(rendered, 4) == 0 && ring.FramesPlayed == 0, "initial gate freezes position below threshold");
ring.Push(samples, 2, () => false);
Check(ring.Render(rendered, 4) == 4 && ring.FramesPlayed == 4, "initial threshold releases buffered PCM");
ring.Render(rendered, 4);
ring.Push(samples, 4, () => false);
Check(ring.Render(rendered, 4) == 0 && ring.FramesPlayed == 4, "underflow uses separate resume threshold and freezes position");
ring.Push(samples, 4, () => false);
Check(ring.Render(rendered, 8) == 8 && ring.FramesPlayed == 12, "resume threshold restarts consumption");
string scope = "stream-test-" + Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", scope);
using var alive = new AliveOwner(IpcConstants.ClientAliveMutexName);
using var server = new HttpFixture();
using var player = new Process { StartInfo = new(Path.GetFullPath(args[0])) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
player.StartInfo.Environment["ORIGINALSOUND_IPC_SCOPE"] = scope;
player.Start();
var output = player.StandardOutput.ReadToEndAsync();
var errors = player.StandardError.ReadToEndAsync();
var client = new StreamingClient();
try
{
    var capabilities = await client.SendAsync(new());
    Check(capabilities.Accepted && capabilities.Capabilities.Contains("prepare"), "isolated real AudioPlayer IPC handshake");
    Guid id = Guid.NewGuid();
    var source = new PlaybackSource
    {
        Kind = PlaybackSourceKind.Http, ResourceId = "tone", Location = server.Url("tone"),
        Headers = new() { ["X-Test-Long"] = new string('x', 6000) },
        Buffer = new() { InitialMs = 200, ResumeMs = 400, CapacityMs = 1000, OpenTimeoutMs = 4000, ReadTimeoutMs = 1000, RetryCount = 0 }
    };
    Check((await client.PrepareAsync(source, id)).Accepted, "descriptor exceeding legacy 2 KB slot accepted");
    var ready = await WaitFor(id, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed);
    Console.WriteLine($"Ready: {ready}");
    Check(ready.Phase == StreamPhase.Ready && ready.BufferedMs >= 200 && !ready.WantsPlay && ready.PositionMs == 0, "prepare fills PCM buffer without playing");
    Check((await client.SeekAsync(id, 2000, 1)).Accepted, "prepared source accepts seek");
    var seek = await WaitFor(id, x => x.SeekId == 1 || x.Phase == StreamPhase.Failed);
    Check(seek.SeekId == 1 && seek.Phase != StreamPhase.Failed, "range seek completes on decoder thread");
    Check(!(await client.SeekAsync(id, -1, 2)).Accepted, "negative seek rejected");
    Check(!(await client.SeekAsync(id, long.MaxValue, 2)).Accepted, "overflowing seek rejected");
    Check(!(await client.SeekAsync(id, 9000, 2)).Accepted, "seek beyond duration rejected");
    var replacement = source with { Location = server.Url("tone2") };
    Check((await client.RefreshSourceAsync(id, replacement)).Accepted, "source URL refresh accepted");
    Check((await WaitFor(id, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed)).Phase == StreamPhase.Ready, "refreshed source is prepared");
    Check((await client.StatusAsync(id)).PositionMs == 2000, "refresh preserves prepared seek position");
    Check((await client.StopAsync(id)).Accepted, "prepared session released");

    Guid slow = Guid.NewGuid();
    Check((await client.PrepareAsync(source with { Location = server.Url("slow") }, slow)).Accepted, "slow open starts asynchronously");
    Check((await client.PlayAsync(slow)).WantsPlay, "play intent during opening is retained");
    Check(!(await client.PauseAsync(slow)).WantsPlay, "pause during opening replaces play intent");
    var timer = Stopwatch.StartNew();
    Check((await client.StopAsync(slow)).Accepted && timer.ElapsedMilliseconds < 2000, "stop interrupts blocked native HTTP open");
    await Task.Delay(300);
    Check(!(await client.StatusAsync(slow)).Accepted, "canceled preparation cannot reappear");

    Guid unauthorized = Guid.NewGuid();
    await client.PrepareAsync(source with { Location = server.Url("unauthorized") }, unauthorized);
    Check((await WaitFor(unauthorized, x => x.Phase == StreamPhase.Failed)).Phase == StreamPhase.Failed, "401 is a failure, not track end");
    await client.StopAsync(unauthorized);
    Guid noSeek = Guid.NewGuid();
    await client.PrepareAsync(source with { CanSeek = false }, noSeek);
    await WaitFor(noSeek, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed);
    Check(!(await client.SeekAsync(noSeek, 1000, 1)).Accepted, "source capability rejects seek");
    await client.StopAsync(noSeek);
    Check(!(await client.PrepareAsync(source with { Headers = new() { ["Bad"] = "a\r\nb" } }, Guid.NewGuid())).Accepted, "header injection rejected");
    Check(!(await client.PrepareAsync(source with { IsLive = true }, Guid.NewGuid())).Accepted, "unsupported live source fails explicitly");
    Guid truncated = Guid.NewGuid();
    await client.PrepareAsync(source with { Location = server.Url("truncated") }, truncated);
    Check((await WaitFor(truncated, x => x.Phase == StreamPhase.Failed)).Phase == StreamPhase.Failed, "truncated HTTP body fails instead of signaling natural end");
    await client.StopAsync(truncated);
    Guid retry = Guid.NewGuid();
    await client.PrepareAsync(source with { Location = server.Url("retry"), Buffer = source.Buffer with { RetryCount = 1 } }, retry);
    Check((await WaitFor(retry, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed)).Phase == StreamPhase.Ready && server.RetryRequests >= 2, "interrupted HTTP transfer retries and prepares successfully");
    await client.StopAsync(retry);
    Guid timeout = Guid.NewGuid();
    var timeoutClock = Stopwatch.StartNew();
    await client.PrepareAsync(source with { Location = server.Url("slow"), Buffer = source.Buffer with { OpenTimeoutMs = 500, ReadTimeoutMs = 500 } }, timeout);
    Check((await WaitFor(timeout, x => x.Phase == StreamPhase.Failed)).Phase == StreamPhase.Failed && timeoutClock.ElapsedMilliseconds < 3000, "open timeout interrupts native I/O");
    await client.StopAsync(timeout);
    using (var tlsServer = new HttpFixture(tls: true))
    {
        using var pinnedClient = new HttpClient(new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == tlsServer.CertificateThumbprint });
        Check((await pinnedClient.GetByteArrayAsync(tlsServer.Url("tone"))).Length > 0, "TLS fixture serves audio using the exact pinned test certificate");
        Guid tls = Guid.NewGuid();
        await client.PrepareAsync(source with { Location = tlsServer.Url("tone") }, tls);
        Check((await WaitFor(tls, x => x.Phase == StreamPhase.Failed)).Phase == StreamPhase.Failed, "HTTPS rejects an untrusted certificate");
        await client.StopAsync(tls);
    }
    Guid firstPending = Guid.NewGuid(), secondPending = Guid.NewGuid();
    await PrepareWhenAvailable(source with { Location = server.Url("slow") }, firstPending);
    await PrepareWhenAvailable(source with { Location = server.Url("slow") }, secondPending);
    Check(!(await client.PrepareAsync(source, Guid.NewGuid())).Accepted, "preparation concurrency is bounded");
    await client.StopAsync(firstPending); await client.StopAsync(secondPending);
    if (args.Contains("--play"))
    {
        Guid audible = Guid.NewGuid();
        await PrepareWhenAvailable(source, audible);
        await WaitFor(audible, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed);
        await client.SeekAsync(audible, 2000, 1);
        Check((await client.PlayAsync(audible)).Accepted, "real device playback requested");
        // No status polling between Seek and Play: committing must not rely on a poll changing the phase.
        await Task.Delay(500);
        var playing = await WaitFor(audible, x => x.PositionMs > 2100 || x.Phase == StreamPhase.Failed);
        if (playing.Phase == StreamPhase.Failed)
            Console.WriteLine("SKIP physical output: device could not be opened; failure is reported instead of permanent buffering.");
        else
        {
            Check(playing.PositionMs > 2100, "prepared seek followed immediately by play advances rendered position");
            await client.PauseAsync(audible);
            var paused = await client.StatusAsync(audible);
            await Task.Delay(250);
            Check((await client.StatusAsync(audible)).PositionMs == paused.PositionMs, "pause freezes rendered position");
            Check((await client.RefreshSourceAsync(audible, replacement)).Accepted, "paused playback URL refresh accepted");
            var refreshed = await WaitFor(audible, x => x.Phase is StreamPhase.Ready or StreamPhase.Failed);
            Check(refreshed.Phase == StreamPhase.Ready && !refreshed.WantsPlay && Math.Abs(refreshed.PositionMs - paused.PositionMs) < 10,
                "URL refresh retains pause intent and rendered position");
            await client.SeekAsync(audible, 7500, 2);
            await client.PlayAsync(audible);
            Check((await WaitFor(audible, x => x.Phase is StreamPhase.Ended or StreamPhase.Failed)).Phase == StreamPhase.Ended,
                "HTTP playback drains to natural end");
            Check(!(await client.PlayAsync(audible)).Accepted, "ended playback requires explicit seek or prepare");
        }
        await client.StopAsync(audible);
    }
    // Leave a slow preparation in flight: releasing the private client mutex must shut down the entire player.
    await PrepareWhenAvailable(source with { Location = server.Url("slow") }, Guid.NewGuid());
    alive.Dispose();
    await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    Check(player.HasExited, "process exits with native open in flight");
    Console.WriteLine("All streaming integration checks passed.");
}
finally
{
    if (!player.HasExited) { player.Kill(true); await player.WaitForExitAsync(); }
    Console.WriteLine(await output); Console.WriteLine(await errors);
}
async Task PrepareWhenAvailable(PlaybackSource source, Guid id)
{
    var timer = Stopwatch.StartNew();
    while (true)
    {
        var reply = await client.PrepareAsync(source, id);
        if (reply.Accepted) return;
        if (reply.Error != "PreparationLimit" || timer.Elapsed > TimeSpan.FromSeconds(8))
            throw new Exception($"Prepare rejected: {reply.Error}");
        await Task.Delay(25);
    }
}
async Task<StreamReply> WaitFor(Guid id, Func<StreamReply, bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(8))
    {
        var result = await client.StatusAsync(id);
        if (condition(result)) return result;
        await Task.Delay(25);
    }
    throw new TimeoutException("Streaming state timeout.");
}
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }

sealed class HttpFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _wave;
    private readonly X509Certificate2? _certificate;
    private int _retryRequests;
    public string? CertificateThumbprint => _certificate?.Thumbprint;
    public int RetryRequests => Volatile.Read(ref _retryRequests);
    public HttpFixture(bool tls = false)
    {
        if (tls)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        using var data = new MemoryStream();
        using (var w = new BinaryWriter(data, Encoding.ASCII, true))
        {
            int samples = 48000 * 8;
            w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVEfmt "u8); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(samples * 2);
            for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * Math.PI * 2 * 440 / 48000) * 500));
        }
        _wave = data.ToArray();
        _listener.Start();
        _ = Listen();
    }
    public string Url(string path) => $"{(_certificate is null ? "http" : "https")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";
    private async Task Listen()
    {
        try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Respond(client); } }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }
    private async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            using Stream stream = _certificate is null ? client.GetStream() : new SslStream(client.GetStream(), false);
            if (stream is SslStream ssl)
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? first = await reader.ReadLineAsync(_stop.Token);
            int start = 0; bool range = false;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                { range = true; int.TryParse(line[13..].Split('-')[0], out start); }
            if (first?.Contains("/slow") == true) { await Task.Delay(30000, _stop.Token); return; }
            if (first?.Contains("/unauthorized") == true)
            { await stream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token); return; }
            start = Math.Clamp(start, 0, _wave.Length);
            string headers = $"HTTP/1.1 {(range ? "206 Partial Content" : "200 OK")}\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: {_wave.Length - start}\r\n";
            if (range) headers += $"Content-Range: bytes {start}-{_wave.Length - 1}/{_wave.Length}\r\n";
            headers += "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
            bool truncated = first?.Contains("/truncated") == true || (first?.Contains("/retry") == true && Interlocked.Increment(ref _retryRequests) == 1);
            await stream.WriteAsync(_wave.AsMemory(start, truncated ? Math.Min(64, _wave.Length - start) : _wave.Length - start), _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or AuthenticationException) { if (_certificate is not null) Console.WriteLine("TLS fixture: " + ex.GetType().Name + ": " + ex.Message); }
    }
    public void Dispose() { _stop.Cancel(); _listener.Stop(); _certificate?.Dispose(); }
}

sealed class AliveOwner : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _end = new();
    private readonly Thread _thread;
    public AliveOwner(string name)
    {
        _thread = new Thread(() => { using var mutex = new Mutex(true, name); _ready.Set(); _end.Wait(); mutex.ReleaseMutex(); });
        _thread.IsBackground = true;
        _thread.Start();
        _ready.Wait();
    }
    public void Dispose() { _end.Set(); _thread.Join(); }
}
