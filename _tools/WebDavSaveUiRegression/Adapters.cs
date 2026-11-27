using Microsoft.UI.Xaml;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;

// Save orchestration, WinUI dispatcher and PasswordVault are real. Database/library/probe boundaries are adapters.
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService
    {
        public int ReadThread;
        public int WriteCount;
        public bool FailSave;
        public WebDavSource? Stored;
        public TaskCompletionSource? CommitGate;
        public TaskCompletionSource CommitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<List<WebDavSource>> GetRemoteSourcesAsync()
        {
            ReadThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(300); // Reproduce a slow synchronous prefix, independent of vault warm-up.
            return Task.FromResult(Stored is null ? new List<WebDavSource>() : new List<WebDavSource> { Stored });
        }
        public async Task SaveWebDavSourceAsync(WebDavSource source)
        {
            CommitStarted.TrySetResult();
            if (CommitGate is not null) await CommitGate.Task;
            await Task.Delay(30);
            if (source.Id == 0) source.Id = 1;
            if (FailSave) throw new IOException("Injected persistence failure after assigning an ID.");
            WriteCount++;
            Stored = Snapshot(source);
        }
        public static WebDavSource Snapshot(WebDavSource source) => new()
        {
            Id = source.Id, Name = source.Name, BaseUri = source.BaseUri, Kind = source.Kind, UserName = source.UserName,
            CredentialKey = source.CredentialKey, ConfigurationRevision = source.ConfigurationRevision
        };
    }
    public sealed class LibraryAdapter
    {
        public int RefreshCount;
        public bool CanPublishState { get; set; } = true;
        public readonly TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? RefreshGate;
        public bool FailRefresh;
        public async Task RefreshSongsSourceAsync(CancellationToken token = default)
        {
            RefreshCount++;
            RefreshStarted.TrySetResult();
            if (RefreshGate is not null) await RefreshGate.Task.WaitAsync(token);
            if (FailRefresh) throw new IOException("Injected post-commit refresh failure.");
        }
    }
    public sealed partial class WebDavLibraryService(MusicDatabaseService database)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly LibraryAdapter _library = new();
        private readonly NullLogger<WebDavLibraryService> logger = NullLogger<WebDavLibraryService>.Instance;
        public int RefreshCount => _library.RefreshCount;
        public LibraryAdapter Projection => _library;
        public readonly TaskCompletionSource ScanStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ScanGate;
        public int ScanCount;
        public event Action? SourcesChanged;
        public Task ProbeAsync(WebDavSource source) => Task.CompletedTask;
        private static void OnSourceConfigurationChanged(WebDavSource previous, bool enabled) { }
        private Task ScanCatalogAsync(WebDavSource source, CancellationToken token) => ScanCoreAsync(source, token);

        private async Task ScanCoreAsync(WebDavSource source, CancellationToken token)
        {
            ScanCount++;
            ScanStarted.TrySetResult();
            try { if (ScanGate is not null) await ScanGate.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async Task StopSavesForTestAsync()
        {
            _stop.Cancel();
            await DrainSourceSavesAsync();
            Task[] scans;
            lock (_gate) scans = _scans.Values.Select(scan => scan.Work).ToArray();
            await Task.WhenAll(scans);
        }
    }
}
namespace WinUIMusicPlayer
{
    public static class App { public static Window MainWindow { get; set; } = null!; }
}
