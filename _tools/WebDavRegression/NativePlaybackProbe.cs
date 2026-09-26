using System.Diagnostics;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Services.WebDav;

/// <summary>Isolated real AudioPlayer decodes and seeks the production bridge without starting audible playback.</summary>
internal static class NativePlaybackProbe
{
    public static async Task RunAsync(string executable, string location, WebDavEntry entry, CancellationToken token)
    {
        using var alive = new AliveOwner(IpcConstants.ClientAliveMutexName);
        using var player = new Process
        {
            StartInfo = new(Path.GetFullPath(executable))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        player.Start();
        var output = player.StandardOutput.ReadToEndAsync();
        var errors = player.StandardError.ReadToEndAsync();
        var client = new StreamingClient();
        var id = Guid.NewGuid();
        try
        {
            var source = new PlaybackSource
            {
                Kind = PlaybackSourceKind.Http, ResourceId = entry.Href, Location = location,
                FileExtension = Path.GetExtension(entry.Name), ContentLength = entry.Length,
                Buffer = new() { InitialMs = 200, ResumeMs = 400, CapacityMs = 1000, RetryCount = 0 }
            };
            if (!(await client.PrepareAsync(source, id, token)).Accepted) throw new Exception("Native preparation rejected.");
            var ready = await WaitAsync(client, id, null, token);
            if (ready.BufferedMs < 200 || ready.WantsPlay) throw new Exception("Native PCM preparation failed.");
            if (!(await client.SeekAsync(id, 30000, 1, token)).Accepted) throw new Exception("Native seek rejected.");
            await WaitAsync(client, id, 1, token);
            await client.StopAsync(id, token);
            Console.WriteLine("PASS: real AudioPlayer prepared PCM and sought to 30 seconds through the OpenList bridge.");
        }
        finally
        {
            alive.Dispose();
            try { await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { player.Kill(true); await player.WaitForExitAsync(); }
            await Task.WhenAll(output, errors);
        }
    }

    private static async Task<StreamReply> WaitAsync(StreamingClient client, Guid id, long? seekId, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var state = await client.StatusAsync(id, token);
            if (state.Phase == StreamPhase.Failed) throw new Exception("Native playback failed: " + state.Error);
            if (seekId is null ? state.Phase == StreamPhase.Ready : state.SeekId == seekId) return state;
            await Task.Delay(30, token);
        }
        throw new TimeoutException("Native preparation/seek timed out.");
    }

    private sealed class AliveOwner : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(), _end = new();
        private readonly Thread _thread;
        private bool _disposed;
        public AliveOwner(string name)
        {
            _thread = new Thread(() =>
            {
                using var mutex = new Mutex(true, name);
                _ready.Set();
                _end.Wait();
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            _thread.Start();
            _ready.Wait();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _end.Set();
            _thread.Join();
            _ready.Dispose();
            _end.Dispose();
        }
    }
}
