namespace ATL
{
    public class Track(string path)
    {
        public string Title => path;
        public string Artist => "Test";
        public string Album => "Test";
    }
}
namespace ZLinq
{
    public static class Extensions
    {
        public static IEnumerable<T> AsValueEnumerable<T>(this IEnumerable<T> source) => source;
    }
}
namespace Windows.Storage
{
    public class StorageFile(string path)
    {
        public string Path => path;
        public string Name => global::System.IO.Path.GetFileName(path);
        public string FileType => global::System.IO.Path.GetExtension(path);
        public static Task<StorageFile> GetFileFromPathAsync(string path) => Task.FromResult(new StorageFile(path));
    }
    public interface IStorageItem { string Path { get; } }
    public class StorageFolder(string path) : IStorageItem
    {
        public string Path => path;
        public string Name => global::System.IO.Path.GetFileName(path);
        public static Task<StorageFolder> GetFolderFromPathAsync(string path) => Task.FromResult(new StorageFolder(path));
        public Task<List<StorageFile>> GetFilesAsync() => Task.FromResult(Directory.GetFiles(path).Select(p => new StorageFile(p)).ToList());
        public Task<List<StorageFolder>> GetFoldersAsync() => Task.FromResult(Directory.GetDirectories(path).Select(p => new StorageFolder(p)).ToList());
    }
}
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static IServiceProvider Services { get; set; } = null!;
        public static TestWindow MainWindow { get; } = new();
        public static Microsoft.Extensions.Logging.ILogger<T> GetLogger<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
    }
}
namespace WinUIMusicPlayer.Model
{
    public class Music
    {
        [SQLite.PrimaryKey, SQLite.AutoIncrement] public int Id { get; set; }
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Album { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string LastLevelFolderPath { get; set; } = "";
        public string Extension { get; set; } = "";
        public TimeSpan Duration { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public int BitDepth { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        public int Channel { get; set; }
        public int TrackNumber { get; set; }
        public int DiskNumber { get; set; }
        public int Year { get; set; }
        public int Order { get; set; }
        public int PlayCount { get; set; }
        public bool IsFavorite { get; set; }
        public int LyricsOffsetMs { get; set; }
    }
    public class UsbDeviceMusic : Music
    {
        public string UniqueDeviceId { get; set; } = "";
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static Action<Model.Music, string> WriteMetadata = (music, path) => File.WriteAllText(path, music.Title);
        public static void SaveMetaData(Model.Music music, string path, byte[]? cover, string? lyrics, string? krc) => WriteMetadata(music, path);
        public static string GetString(string key) => key;
        public static TaskCompletionSource SlowFile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static int Reads;
        public static bool IsMusicFile(string extension) => extension == ".mp3";
        public static async Task<(Model.Music?, string?)> GetMusicInfo(Windows.Storage.StorageFile file)
        {
            if (file.Name.StartsWith("slow")) await SlowFile.Task;
            Interlocked.Increment(ref Reads);
            if (file.Name.StartsWith("broken")) throw new IOException("Test read failure");
            return (new Model.Music
            {
                Path = file.Path, Title = file.Name, FolderPath = global::System.IO.Path.GetDirectoryName(file.Path)!,
                LastLevelFolderPath = new DirectoryInfo(global::System.IO.Path.GetDirectoryName(file.Path)!).Name,
                UpdateTime = File.GetLastWriteTime(file.Path), Duration = TimeSpan.FromSeconds(2), SampleRate = 48000
            }, "lyrics");
        }
    }
}
