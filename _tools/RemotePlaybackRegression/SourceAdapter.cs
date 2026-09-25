using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    // Only persistence, credentials and source settings are adapted. Availability, cache, HTTP,
    // loopback, IPC monitoring, recovery and UI dispatch all run production implementations.
    public sealed class MusicDatabaseService
    {
        public Task<List<WebDavSource>> GetWebDavSourcesAsync() => Task.FromResult<List<WebDavSource>>([new(10)]);
    }
    public sealed partial class WebDavLibraryService(MusicDatabaseService database, WebDavTransport transport,
        RemoteAudioCache cache, ILogger<WebDavLibraryService> logger) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private AppViewModel? _library;
        public WebDavEntry Entry = new("/dav/tone.flac", "tone.flac", false, 3 * 1024 * 1024 + 137, "\"v1\"", null);
        public string Root = "";
        public bool RejectCredentials;
        public int MissingTrack;
        public int Connects;
        public event Action<int, bool>? SourceAvailabilityChanged;
        public void Attach(AppViewModel state) => _library = state;
        public WebDavConnection Connect(WebDavSource source)
        {
            Interlocked.Increment(ref Connects);
            if (RejectCredentials) throw new InvalidOperationException("cached playback requested credentials");
            return new(new Uri(Root), "", "");
        }
        public Task<(WebDavSource Source, WebDavEntry Track)> ResolveAsync(Music music) => music.Id == MissingTrack
            ? Task.FromException<(WebDavSource, WebDavEntry)>(new WebDavException("ResourceMissing"))
            : Task.FromResult((new WebDavSource(music.SourceId), Entry));
        public static WebDavEntry ToEntry(WebDavEntry entry) => entry;
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            Task[] pending;
            lock (_gate) pending = [.. _probeWork];
            await Task.WhenAll(pending);
            _stop.Dispose();
        }
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class WebDavText { public static string Error(string code) => code; }
}
