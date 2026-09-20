using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Model
{
    public sealed record Music(int Id, string Path)
    {
        public string Author { get; init; } = "artist";
        public string Album { get; init; } = "album";
        public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.');
        public string Title { get; init; } = "title";
        public string LastLevelFolderPath { get; init; } = "folder";
        public int DiskNumber { get; init; }
        public int TrackNumber { get; init; }
        public int Order { get; init; }
        public DateTime CreateTime { get; init; }
        public DateTime UpdateTime { get; init; }
    }
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public int FillCount { get; private set; }
        public void FillFrom(ReadOnlySpan<T> items)
        {
            FillCount++;
            Clear();
            foreach (var item in items) Add(item);
        }
        public BulkObservableCollection() { }
        public BulkObservableCollection(IEnumerable<T> items) : base(items) { }
        public void AddRange(IEnumerable<T> items) { foreach (var item in items) Add(item); }
        public void InsertRange(int index, IEnumerable<T> items) { foreach (var item in items) Insert(index++, item); }
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public enum PlayMode { SingleLoop, ListLoop, RandomLoop, RepeatOff }
        public static string SanitizeFileName(string text, char[] invalid) => text;
        public static string ConvertLyrics(string text) => text;
        public static string GetFirstLetterAdvanced(string text) => text.Length == 0 ? "#" : text[..1];
    }
}
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static TestWindow MainWindow { get; } = new();
        public static Microsoft.Extensions.Logging.ILogger<T> GetLogger<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
    public sealed class TestWindow
    {
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class AudioConverterService
    {
        public event EventHandler<double>? updateProgress;
        public bool Succeed { get; set; }
        public static string GetExtensionForFormat(string format) => format;
        public async Task<bool> ConvertForExportAsync(Model.Music music, string path, string format, int rate)
        {
            await Task.Yield();
            updateProgress?.Invoke(this, 100);
            if (Succeed) await File.WriteAllTextAsync(path, "converted");
            return Succeed;
        }
    }
    public sealed class MusicDatabaseService
    {
        public Task<(string?, string?, string?, string?)> GetLyricsAsync(int id) => Task.FromResult<(string?, string?, string?, string?)>((null, null, null, null));
    }
}
namespace WinUIMusicPlayer.Extensions
{
    public static class Extensions
    {
        public static WinUIMusicPlayer.Model.BulkObservableCollection<T> CreateShuffled<T>(this ObservableCollection<T> items)
            => new(items.Reverse());
    }
}
