using System.Security.Cryptography;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;

internal static class BulkUsbRegression
{
    internal static async Task RunAsync(UsbStorageDevice device, int count, Func<byte[]> createWave)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        string author = "OriginalSound-UsbBulk-" + Guid.NewGuid().ToString("N");
        using var services = new TestFiles();
        var database = new MusicDatabaseService();
        var root = await UsbStorageDeviceReader.OpenPortableStorageAsync(device);
        Console.WriteLine($"Isolated bulk folder: MUSIC/{author}; songs: {count}");
        try
        {
            await database.InitializeAsync(Path.Combine(services.Path, "usb.db"));
            string[] albums = ["正常专辑", "Sony Music Labels Inc.", "攻殻機動隊 新劇場版 O.S.T.", "正常专辑",
                "lulu.", "wyxt.", ".", "..", "CON", "LPT1.mix", "空格专辑   ", "AUX", "COM²", "带\"非法:字符?"];
            var songs = new List<Music>(count);
            byte[] data = createWave();
            string expectedHash = Convert.ToHexString(SHA256.HashData(data));
            for (int i = 0; i < count; i++)
            {
                string path = Path.Combine(services.Path, $"track-{i:D4}.wav");
                await File.WriteAllBytesAsync(path, data);
                songs.Add(new Music(path, i > 0 && i % 13 == 0 ? author + ". " : author, albums[i % albums.Length]));
            }
            var converter = new AudioConverterService(new WinUIMusicPlayer.ViewModel.AppViewModel(), WinUIMusicPlayer.App.GetLogger<AudioConverterService>());
            var writer = new UsbWriterHelper(converter, database);
            var progress = new CaptureProgress();
            var sent = await writer.WriteToUsb(songs, device, progress: progress);
            Console.WriteLine($"Bulk result: {sent.Succeeded.Count}/{count} audio files; reported progress: {progress.Latest:F1}%");
            Check(sent.Succeeded.Count == count, "Every bulk audio file must finish, including the logged trailing-dot album names.");
            await database.SaveUsbExportRecordsAsync(sent.Succeeded, device);
            var authorFolder = await (await root.GetFolderAsync("MUSIC")).GetFolderAsync(author);
            foreach (var song in songs)
            {
                var album = await authorFolder.GetFolderAsync(UsbExportPaths.GetFolderName(song.Album));
                var audio = await album.GetFileAsync(Path.GetFileName(song.Path));
                using var stream = await audio.OpenStreamForReadAsync();
                Check(Convert.ToHexString(await SHA256.HashDataAsync(stream)) == expectedHash, "Bulk audio bytes differ.");
                string stem = Path.GetFileNameWithoutExtension(song.Path);
                await album.GetFileAsync(stem + ".lrc");
                await album.GetFileAsync(stem + "_Translated.lrc");
                await album.GetFileAsync(stem + "_Pronunciation.lrc");
            }
            var stored = (await database.GetUsbDeviceMusics(device.UniqueId)).Where(record => record.Path.Contains(author)).ToList();
            Check(stored.Count == count, "Every bulk success must have exactly one ledger record.");
            var paths = stored.ToDictionary(record => record.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var song in songs)
            {
                var record = paths[UsbExportPaths.GetRelativeFilePath(song, "wav")];
                Check(record.Author == song.Author && record.Album == song.Album, "Folder sanitization must preserve original metadata.");
            }
            Console.WriteLine($"PASS: {count} real MTP audio hashes, {count * 3} lyrics, matching ledger paths and original metadata.");
        }
        finally
        {
            try
            {
                var folder = await (await root.GetFolderAsync("MUSIC")).GetFolderAsync(author);
                Check(folder.Name == author, "Unsafe bulk cleanup target.");
                foreach (var album in await folder.GetFoldersAsync())
                {
                    foreach (var file in await album.GetFilesAsync()) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    await album.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (FileNotFoundException) { }
            finally { await database.CloseAsync(); }
            Console.WriteLine("Cleaned isolated bulk phone folder.");
        }
        Console.WriteLine("PASS: all bulk USB integration checks completed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureProgress : IProgress<double>
    {
        private double _latest;
        internal double Latest => Volatile.Read(ref _latest);
        public void Report(double value) => Volatile.Write(ref _latest, value);
    }

    private sealed class TestFiles : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("OriginalSound-UsbBulk-").FullName;
        public void Dispose()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!System.IO.Path.GetFullPath(Path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe local bulk cleanup target.");
            Directory.Delete(Path, true);
        }
    }
}
