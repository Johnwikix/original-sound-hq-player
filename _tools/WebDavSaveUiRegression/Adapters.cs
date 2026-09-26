using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;

// Save orchestration, WinUI dispatcher and PasswordVault are real. Database/library/probe boundaries are adapters.
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService
    {
        public int ReadThread;
        public int WriteCount;
        public bool FailSave;
        public TaskCompletionSource? CommitGate;
        public TaskCompletionSource CommitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<List<WebDavSource>> GetWebDavSourcesAsync()
        {
            ReadThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(300); // Reproduce a slow synchronous prefix, independent of vault warm-up.
            return Task.FromResult(new List<WebDavSource>());
        }
        public async Task SaveWebDavSourceAsync(WebDavSource source)
        {
            CommitStarted.TrySetResult();
            if (CommitGate is not null) await CommitGate.Task;
            await Task.Delay(30);
            if (FailSave) throw new IOException("Injected persistence failure.");
            WriteCount++;
            if (source.Id == 0) source.Id = 1;
        }
    }
    public sealed class LibraryAdapter
    {
        public int RefreshCount;
        public Task RefreshSongsSourceAsync(CancellationToken token = default) { RefreshCount++; return Task.CompletedTask; }
    }
    public sealed partial class WebDavLibraryService(MusicDatabaseService database)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly LibraryAdapter _library = new();
        public int RefreshCount => _library.RefreshCount;
        public event Action? SourcesChanged;
        public Task ProbeAsync(WebDavSource source) => Task.CompletedTask;
        public async Task StopSavesForTestAsync() { _stop.Cancel(); await DrainSourceSavesAsync(); }
    }
}
namespace WinUIMusicPlayer
{
    public static class App { public static Window MainWindow { get; set; } = null!; }
}
