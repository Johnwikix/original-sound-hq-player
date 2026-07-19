using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class AppInitializerService : IHostedService
    {
        private readonly AppViewModel AppViewModel;
        private readonly MusicDatabaseService MusicDatabaseService;
        public AppInitializerService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
        }
        // 应用启动时执行初始化
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var ipcService = App.Services.GetRequiredService<IpcService>();
            var musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();

            await MusicDatabaseService.Initialize();
            await ipcService.InitializingAsync();
            await Task.WhenAll(
                MusicDatabaseService.GetEqualizerSettingsAsync(),
                MusicDatabaseService.GetSettingsAsync());
            MusicDatabaseService.LoadWindowState();
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            App.MainWindow.Activate();
            await Task.Run(() => RunLongOpsAsync(MusicDatabaseService, cancellationToken), cancellationToken);
            ToolUtils.CleanupStaleCacheFiles();
            await musicBrowseViewModel.LoadPlayStateToMusicBrowsePage();
            await ipcService.InitializeMusic(appViewModel.CurrentPlayingMusic);
            App.MainWindow.ShowMainPage();
            appViewModel.IsInitialized = true;
            appViewModel.InitHotKeys();
            await CheckVersionUpdateAsync();
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task CheckVersionUpdateAsync()
        {
            try
            {
                string recordedVersion = await MusicDatabaseService.GetRecordedVersionAsync();
                string? currentVersion = AppViewModel.Version;
                if (!string.IsNullOrEmpty(currentVersion) && currentVersion != recordedVersion)
                {
                    string? notes = await ReadUpdateNotesAsync();
                    if (notes is not null)
                    {
                        var dialog = new View.SubView.UpdateHistoryDialog(
                            currentVersion, notes, "https://github.com/Johnwikix/original-sound-hq-player");
                        await dialog.ShowThemedAsync(App.MainWindow.Content.XamlRoot);
                    }
                    await MusicDatabaseService.SaveCurrentVersionAsync(currentVersion);
                }
            }
            catch (Exception ex)
            {
                var logger = App.GetLogger<AppInitializerService>();
                logger.LogError(ex, $"CheckVersionUpdateAsync 检查版本更新时出错: {ex.Message}");
            }
        }

        private static async Task<string?> ReadUpdateNotesAsync()
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(
                    new Uri("ms-appx:///UpdateNotes.json"));
                string json = await FileIO.ReadTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                string lang = AppData.SystemLanguage == "zh" ? "zh-CN" : "en";
                return doc.RootElement.TryGetProperty(lang, out var notes) ? notes.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task RunLongOpsAsync(MusicDatabaseService db, CancellationToken ct)
        {
            await InitialFileScan.InitialScan();
            await db.LoadMusicList();
            await db.GetPlayStateAsync();
        }
    }
}
