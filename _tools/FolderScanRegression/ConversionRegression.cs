using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class ConversionRegression
{
    internal static async Task RunAsync(string fixture)
    {
        FFmpeg.AutoGen.ffmpeg.RootPath = Path.GetFullPath("Libraries/FFmpeg/x64");
        string root = Directory.CreateDirectory(Path.Combine(fixture, "conversion")).FullName;
        var database = new MusicDatabaseService(Path.Combine(fixture, "conversion.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlayListMusic>();
        database.Versions = new(database.Connection);
        await database.Versions.InitializeAsync();
        await database.Connection.InsertAsync(new Folder { Name = "conversion", Path = root, Type = Folder.TypeLocal });
        var state = new AppViewModel();
        var converter = new AudioConverterService(state, NullLogger<AudioConverterService>.Instance);
        var tasks = new ApplicationTasks(new AppLifecycle());
        var vm = new AudioConversionViewModel(converter, state, tasks, database);
        var watcher = new LibraryWatcherService(database, state, NullLogger<LibraryWatcherService>.Instance);
        var previousServices = App.Services;
        using var services = new ServiceCollection().AddSingleton(database).BuildServiceProvider();
        App.Services = services;
        List<string> failures = [];
        try
        {
            var first = Wave("first");
            await vm.ConvertAsync([first], "mp3:128");
            ExpectPublications(1, "single conversion with watcher off");
            var original = (await database.GetMusicListAsync()).Single();
            await vm.ConvertAsync([first], "mp3:192");
            var overwritten = (await database.GetMusicListAsync()).Single();
            if (overwritten.Id != original.Id || overwritten.MetadataRevision != original.MetadataRevision + 1 ||
                overwritten.UpdateTime != File.GetLastWriteTime(overwritten.Path)) failures.Add("overwritten output retains stale metadata");
            ExpectPublications(2, "overwrite with watcher off");

            var missing = new Music { Title = "missing", Path = Path.Combine(root, "missing.wav"), Extension = "wav" };
            await vm.ConvertAsync([first, first], "wav");
            await vm.ConvertAsync([missing], "mp3");
            ExpectPublications(2, "all skipped or failed");

            byte[] previousOutput = await File.ReadAllBytesAsync(overwritten.Path);
            int failOnce = 1;
            void InterruptEncoder(object? sender, double progress)
            {
                if (progress > 0 && Interlocked.Exchange(ref failOnce, 0) == 1)
                    throw new IOException("Injected after FFmpeg started encoding");
            }
            converter.updateProgress += InterruptEncoder;
            try { await vm.ConvertAsync([first], "mp3:96"); }
            finally { converter.updateProgress -= InterruptEncoder; }
            var afterFailure = await File.ReadAllBytesAsync(overwritten.Path);
            if (failOnce != 0 || !previousOutput.SequenceEqual(afterFailure) ||
                Directory.EnumerateFiles(root, "*.tmp").Any()) failures.Add("interrupted encoding damaged the old output or leaked a temporary file");
            ExpectPublications(2, "interrupted encoder");

            var pending = new PendingMetadataWrite { Path = overwritten.Path, MusicId = overwritten.Id };
            await database.Connection.InsertAsync(pending);
            await vm.ConvertAsync([first], "mp3:96");
            var afterConflict = await File.ReadAllBytesAsync(overwritten.Path);
            if (!previousOutput.SequenceEqual(afterConflict) ||
                await database.Connection.FindAsync<PendingMetadataWrite>(pending.Path) is null)
                failures.Add("conversion replaced the file underneath a queued tag edit");
            await database.Connection.DeleteAsync(pending);
            ExpectPublications(2, "pending metadata edit");

            state.IsFolderWatchEnabled = true;
            await watcher.StartAsync();
            var second = Wave("second");
            var third = Wave("third");
            await vm.ConvertAsync([second, missing, third], "mp3");
            await Task.Delay(1600);
            if ((await database.GetMusicListAsync()).Count != 3) failures.Add("partially successful batch lost an output");
            ExpectPublications(3, "mixed batch and its native watcher notifications");

            var late = new WatchedFolderChanges();
            foreach (var file in Directory.EnumerateFiles(root, "*.mp3")) late.Files.Add(file);
            await AutoRescanService.AutoScan(database, new() { [root] = late }, default);
            ExpectPublications(3, "replayed output events");
            await vm.ConvertAsync([first], "aac:160");
            string m4aPath = Path.Combine(root, "first_output.m4a");
            var aac = (await database.GetMusicListAsync()).Single(song => song.Path == m4aPath);
            if (!FFmpegMetadataProbe.TryProbe(m4aPath, out var aacInfo) || aacInfo.DurationMs <= 0)
                failures.Add("staged M4A output is unreadable");
            await vm.ConvertAsync([first], "alac");
            var alac = (await database.GetMusicListAsync()).Single(song => song.Path == m4aPath);
            if (aac.Id != alac.Id || !FFmpegMetadataProbe.TryProbe(m4aPath, out var alacInfo) || alacInfo.BitDepth <= 0)
                failures.Add("replacing AAC with ALAC at the same output path lost the row or the encoded audio");
            string export = Path.Combine(fixture, "control.m4a");
            if (!await converter.ConvertForExportAsync(first, export, "alac"))
                throw new InvalidOperationException("could not encode M4A control");
            string originalBrand = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(export), 8, 4);
            string stagedBrand = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(m4aPath), 8, 4);
            if (originalBrand != stagedBrand)
                failures.Add($"staging changed the M4A container brand from '{originalBrand}' to '{stagedBrand}'");
            ExpectPublications(5, "AAC and ALAC share an output extension");

            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            Console.WriteLine("PASS: FFmpeg conversion command, indexed overwrite, no-op/failure, partial batch and watcher replay preserve incremental publications.");
        }
        finally
        {
            await watcher.StopAsync();
            App.Services = previousServices;
            await database.Connection.CloseAsync();
        }

        Music Wave(string name)
        {
            string path = Path.Combine(root, name + ".wav");
            File.Copy("_tools/test_tone.wav", path);
            return new Music { Path = path, Title = name, Extension = "wav" };
        }

        void ExpectPublications(int expected, string scenario)
        {
            if (state.FullRefreshes != 0 || database.WatchPublications != expected)
                failures.Add($"{scenario}: full={state.FullRefreshes}, incremental={database.WatchPublications}, expected full=0, incremental={expected}");
        }
    }
}

namespace WinUIMusicPlayer.View.SubView
{
    internal sealed class ProgressDialog(string message)
    {
        public string Title { get; set; } = message;
        public Task UpdateProgress(int value) => Task.CompletedTask;
        public Task ShowThemedAsync(object root) => Task.CompletedTask;
    }
}
