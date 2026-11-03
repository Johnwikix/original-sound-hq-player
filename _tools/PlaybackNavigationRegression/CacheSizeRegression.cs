using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class CacheSizeRegression
{
    internal static async Task RunAsync()
    {
        string previousRoot = AppSettings.MusicCoverCache;
        string root = Path.Combine(AppContext.BaseDirectory, "cache-size-" + Guid.NewGuid().ToString("N"));
        var app = new AppViewModel();
        var model = new WebDavSourcesViewModel(new MusicDatabaseService(), new WebDavLibraryService(),
            new RemotePlaybackService(), new WinUIMusicPlayer.Services.WebDav.RemoteAudioCache(), app, new LibraryQueries());
        bool published = false;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(model.CacheSize)) return;
            if (!WinUIMusicPlayer.App.MainWindow.DispatcherQueue.HasThreadAccess)
                throw new Exception("缓存大小必须在 UI 线程发布");
            published = true;
        };
        try
        {
            Check(CacheSizeCalculator.GetSize(root) == 0, "未建立缓存目录时大小应为零");
            // 每种缓存大小不同，以便发现遗漏或重复统计；包含隐藏文件与在途下载。
            Write("net-cover.bin", 1);
            Write("Cache/image_raw.bin", 2);
            Write("Cache/image_150.bmp", 4);
            Write("Cache/image_raw_display1536_v1.png", 8);
            Write("WebDav/Covers/remote_raw.bin", 16);
            Write("WebDav/Covers/remote_raw_display1536_v1.png", 32);
            Write("WebDav/Audio/song.audio", 64);
            Write("WebDav/Audio/song.audio.123.part", 128);
            File.SetAttributes(Path.Combine(root, "WebDav/Audio/song.audio.123.part"), FileAttributes.Hidden);
            Write("Analysis/Features.db", 256);
            Write("Analysis/Loudness/tone.bin", 512);
            Write("unrelated.mp3", 512);
            Write("Music/album.flac", 1024);
            Check(CacheSizeCalculator.GetSize(root) == 1023, "应合计全部封面和 WebDAV 缓存，排除用户音乐");
            AppSettings.MusicCoverCache = root;
            await model.RefreshCacheSizeAsync();
            Check(published && model.CacheSize == "1023 B", "ViewModel 应发布全部缓存大小");

            // 一次扫描尚未发布时切换目录，旧结果不得覆盖新根目录。
            Task oldRefresh = model.RefreshCacheSizeAsync();
            AppSettings.MusicCoverCache = Path.Combine(root, "empty");
            Task newRefresh = model.RefreshCacheSizeAsync();
            await Task.WhenAll(oldRefresh, newRefresh);
            Check(model.CacheSize == "0 B", "切换根目录后应丢弃旧统计结果");
            AppSettings.MusicCoverCache = root;
            File.Delete(Path.Combine(root, "Cache/image_150.bmp"));
            await model.RefreshCacheSizeAsync();
            Check(model.CacheSize == "1019 B", "清理后应重新计算总量");
            Task stopping = model.RefreshCacheSizeAsync();
            string beforeStop = model.CacheSize;
            await model.StopAsync();
            Check(stopping.IsCompleted && model.CacheSize == beforeStop, "退出应等待扫描且不再发布结果");
            Check(CacheSizeCalculator.FormatSize(1024).EndsWith(" KiB") &&
                CacheSizeCalculator.FormatSize(1024 * 1024).EndsWith(" MiB") &&
                CacheSizeCalculator.FormatSize(1024L * 1024 * 1024).EndsWith(" GiB"), "大小单位应随容量变化");
            Console.WriteLine("PASS: 全部缓存统计、目录切换、清理刷新、UI 通知与退出排空");
        }
        finally
        {
            await model.StopAsync();
            AppSettings.MusicCoverCache = previousRoot;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        void Write(string relative, int bytes)
        {
            string file = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[bytes]);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
