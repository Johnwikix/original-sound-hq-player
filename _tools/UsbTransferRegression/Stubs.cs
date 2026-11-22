namespace WinUIMusicPlayer.Model
{
    public sealed class Music(string path, string author, string album) : System.ComponentModel.INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string Path { get; set; } = path;
        public string Author { get; set; } = author;
        public string Album { get; set; } = album;
        private int _isExistOnDevice;
        public int IsExistOnDevice
        {
            get => _isExistOnDevice;
            set
            {
                if (_isExistOnDevice == value) return;
                _isExistOnDevice = value;
                PropertyChanged?.Invoke(this, new(nameof(IsExistOnDevice)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.');
        public string Title { get; set; } = "测试标题（与文件名不同）";
        public bool IsCueTrack => CueTrackNo > 0;
        public string CuePath { get; set; } = "";
        public int CueTrackNo { get; set; }
        public int BitDepth { get; set; }
        public int SampleRate { get; set; }
        public long StartSample { get; set; }
        public long EndSample { get; set; }
        public bool IsRemote => false;
        public int TrackNumber => CueTrackNo > 0 ? CueTrackNo : 1;
        public int DiskNumber => 1;
        public int Year => 2026;
    }
}

namespace WinUIMusicPlayer.Services
{
    public sealed partial class MusicDatabaseService
    {
        private SQLite.SQLiteAsyncConnection _dbConnection = null!;
        public async Task InitializeAsync(string path)
        {
            _dbConnection = new(path);
            await _dbConnection.CreateTableAsync<Model.UsbDeviceMusic>();
        }
        public Task<List<Model.UsbDeviceMusic>> GetUsbDeviceMusics(string id)
            => _dbConnection.Table<Model.UsbDeviceMusic>().Where(m => m.UniqueDeviceId == id).ToListAsync();
        public Task CloseAsync() => _dbConnection?.CloseAsync() ?? Task.CompletedTask;
        public string? LyricsErrorForTitle { get; set; }
        public Task ScanUsbDeviceAsync(string path, string id) => throw new NotSupportedException("This fixture tests MTP storage only.");
        public Task<Model.LyricsDocument> GetLyricsDocumentAsync(Model.Music music) => music.Title == LyricsErrorForTitle
            ? Task.FromException<Model.LyricsDocument>(new IOException("Controlled lyrics read failure")) : Task.FromResult(
            new Model.LyricsDocument(new("[00:00.00]USB regression", Model.LyricsFormat.Lrc), "[00:00.00]翻译", "[00:00.00]pronunciation"));
        internal Task InstallConvertedFileAsync(string temporaryPath, string outputPath, Action? onCommitted, bool overwrite = true) => throw new InvalidOperationException("USB export must not add staged files to the library.");
    }
}

namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel
    {
        public int DsdPcmFreq => 0;
        public int DsdGain => 0;
    }
}

namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
#if USB_UI_REGRESSION
        public static string GetString(string key) => new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader().GetString(key);
#else
        public static string GetString(string key) => key == "UsbExportPathConflict" ? "USB target path conflicts: {0}" : key;
#endif
        public static string SanitizeFileName(string value, char[] invalid)
        {
            foreach (char c in invalid) value = value.Replace(c, '_');
            return value;
        }
        public static bool IsMusicFile(string extension) => extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
        public static byte[]? CoverBytes { get; set; }
        public static Task<byte[]?> GetRawImage(Model.Music music) => Task.FromResult(CoverBytes);
    }
}
