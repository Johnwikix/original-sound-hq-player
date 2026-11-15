using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using ProductionApp = WinUIMusicPlayer.App;

namespace UsbUiRegression;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApplication();
        });
    }
}

public sealed partial class RegressionApplication : Application
{
    private readonly List<string> _results = [];
    private readonly string _resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
    public RegressionApplication() => InitializeComponent();

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _results.Add("PASS: " + message);
        File.WriteAllLines(_resultPath, _results);
    }

    private static async Task Wait(Func<bool> condition, string message)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        HostWindow? window = null;
        UsbDeviceService? devices = null;
        ApplicationTasks? tasks = null;
        var database = ProductionApp.Services.GetRequiredService<MusicDatabaseService>();
        string local = Directory.CreateTempSubdirectory("OriginalSound-UsbUi-").FullName;
        string author = "OriginalSound-UsbUi-" + Guid.NewGuid().ToString("N");
        StorageFolder? root = null;
        bool passed = false;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            window = new HostWindow();
            ProductionApp.MainWindow = window;
            window.AppWindow.Resize(new(640, 480));
            window.AppWindow.Move(new(-30000, -30000));
            window.AppWindow.Show(false);
            await Wait(() => window.Loaded, "native ListView load");
            foreach (string key in new[] { "UsbExportSummary", "UsbExportIncomplete", "UsbExportStatusFailed", "UsbExportFailureExamples" })
                Check(!string.IsNullOrEmpty(ToolUtils.GetString(key)) && ToolUtils.GetString(key) != key, "PRI resolves " + key);

            var phone = (await UsbStorageDeviceReader.GetUsbStorageDevicesAsync()).First(d => d.IsPortable);
            root = await UsbStorageDeviceReader.OpenPortableStorageAsync(phone);
            await database.InitializeAsync(Path.Combine(local, "ledger.db"));
            devices = new(database, ProductionApp.GetLogger<UsbDeviceService>());
            devices.DeviceMusicChanged += (_, e) => UsbDeviceMusicPresence.Update(window.Songs, devices.MusicOnDevice, e.ChangedTitles, e.ChangedFileNames);
            devices.Devices.Add(phone);
            devices.SelectedDevice = phone;
            using var scanPause = devices.PauseScanForExport();
            var state = window.State;
            state.Lifecycle.TransitionTo(AppPhase.Initializing);
            state.Lifecycle.TransitionTo(AppPhase.Ready);
            tasks = new(state.Lifecycle);
            var converter = new AudioConverterService(new(), ProductionApp.GetLogger<AudioConverterService>());
            var coordinator = new UsbExportCoordinator(state, tasks, converter, database, devices, ProductionApp.GetLogger<UsbExportCoordinator>());
            var first = new Music(Path.Combine(local, "first.wav"), author, "Sony Music Labels Inc.") { Title = "First valid" };
            var missing = new Music(Path.Combine(local, "missing.wav"), author, "lulu.") { Title = "Missing source" };
            var last = new Music(Path.Combine(local, "last.wav"), author, "wyxt.") { Title = "Last valid" };
            await File.WriteAllBytesAsync(first.Path, CreateWave());
            await File.WriteAllBytesAsync(last.Path, CreateWave());
            window.Songs.Add(first);
            window.Songs.Add(missing);
            window.Songs.Add(last);
            await Wait(() => window.Songs.All(m => window.BoundState(m) == "0"), "realized native x:Bind rows");

            Task export = Task.CompletedTask;
            int changes = 0;
            var rowErrors = new List<string>();
            foreach (var song in window.Songs)
                song.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(Music.IsExistOnDevice) || song.IsExistOnDevice != 2) return;
                    changes++;
                    if (!window.DispatcherQueue.HasThreadAccess) rowErrors.Add("row notification outside UI thread");
                    if (export.IsCompleted || !state.Operations.IsActive) rowErrors.Add("row changed after export completed");
                    if (window.BoundState(song) != "2") rowErrors.Add("compiled binding not updated for " + song.Title);
                };
            double maxProgress = 0;
            state.Operations.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(state.Operations.Percent)) maxProgress = Math.Max(maxProgress, state.Operations.Percent);
            };
            export = coordinator.ExportAsync(window.Songs, phone, null, 320);
            await export;
            await Task.Yield();
            Check(changes == 2 && rowErrors.Count == 0, "each copied audio updates native ListView x:Bind during the active batch on the UI thread: " + string.Join(", ", rowErrors));
            Check(first.IsExistOnDevice == 2 && missing.IsExistOnDevice == 0 && last.IsExistOnDevice == 2, "failed audio stays unmarked while later audio still succeeds");
            string expected = string.Format(ToolUtils.GetString("UsbExportSummary"), phone.Name, 2, 3, 1, 0);
            Check(window.SummaryVisible && window.BoundSummary.Contains(expected) && window.BoundSummary.Contains(missing.Title), "native InfoBar reports 2/3 audio, one unsent, no lyric failure and the affected title");
            Check(maxProgress <= 95, "partial batch never reports 100 percent");
            Check((await database.GetUsbDeviceMusics(phone.UniqueId)).Count == 2, "real SQLite ledger contains only successful audio");

            maxProgress = 0;
            state.Shell.InfoBarIsOpen = false;
            export = coordinator.ExportAsync(new[] { first, last }, phone, null, 320);
            await export;
            Check(maxProgress == 100 && !state.Shell.InfoBarIsOpen && changes == 2, "successful overwrite reaches 100 after commit without resetting existing row icons");

            await File.WriteAllBytesAsync(missing.Path, CreateWave());
            database.LyricsErrorForTitle = missing.Title;
            state.Shell.InfoBarIsOpen = false;
            maxProgress = 0;
            export = coordinator.ExportAsync(new[] { missing }, phone, null, 320);
            await export;
            expected = string.Format(ToolUtils.GetString("UsbExportSummary"), phone.Name, 1, 1, 0, 1);
            Check(changes == 3 && rowErrors.Count == 0 && missing.IsExistOnDevice == 2 && window.BoundState(missing) == "2", "lyrics failure preserves the immediately updated successful audio icon");
            Check(window.SummaryVisible && window.BoundSummary.Contains(expected) && maxProgress <= 95,
                "lyrics-only failure reports 1/1 audio, zero unsent, one lyric failure without 100 percent");
            Check((await database.GetUsbDeviceMusics(phone.UniqueId)).Count == 3, "lyrics-only failure still commits successful audio to SQLite");
            passed = true;
        }
        catch (Exception ex)
        {
            _results.Add("FAIL: " + ex);
        }
        finally
        {
            try
            {
                window?.State.Lifecycle.TryBeginExit(out _);
                if (devices is not null) await devices.StopAsync();
                if (tasks is not null) await tasks.DrainAsync();
                await database.CloseAsync();
                if (root is not null)
                {
                    var folder = await (await root.GetFolderAsync("MUSIC")).GetFolderAsync(author);
                    foreach (var album in await folder.GetFoldersAsync())
                    {
                        foreach (var file in await album.GetFilesAsync()) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        await album.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                Directory.Delete(local, true);
                _results.Add("PASS: isolated phone and local fixtures cleaned");
            }
            catch (Exception ex) { passed = false; _results.Add("FAIL: cleanup: " + ex); }
            if (passed) _results.Add("PASS: all native USB UI regression checks completed.");
            File.WriteAllText(_resultPath, string.Join("\r\n", _results) + "\r\n");
            Environment.ExitCode = passed ? 0 : 1;
            window?.Close();
            Exit();
        }
    }

    private static byte[] CreateWave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + 88200);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(44100);
        writer.Write(176400);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(88200);
        writer.Write(new byte[88200]);
        return stream.ToArray();
    }
}
