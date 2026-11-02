using SQLite;
using WinUIMusicPlayer.Services.AudioAnalysis;
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel
    {
        public event Action? SongsSourceChanged;
        public void Notify() => SongsSourceChanged?.Invoke();
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService(AnalysisRepository repository)
    {
        public AnalysisRepository Analysis => repository;
        public Task WhenInitialized => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed record WebDavEntry(string Href, string Name, bool IsDirectory, long Length, string ETag, DateTimeOffset? Modified);
}
