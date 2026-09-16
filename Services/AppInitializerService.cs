using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.DesktopLyrics;
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
            await MusicDatabaseService.Initialize();
            var ipcService = App.Services.GetRequiredService<IpcService>();
            var musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            await ipcService.InitializingAsync();
            await Task.WhenAll(
                MusicDatabaseService.GetEqualizerSettingsAsync(),
                MusicDatabaseService.GetSettingsAsync());
            MusicDatabaseService.LoadWindowState();
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            App.MainWindow.Activate();
            // HWND 已可用；Store 查询与其余启动工作并行，首推设置前再汇合。
            var licenseInitialization = App.Services.GetRequiredService<LicenseService>().InitializeAsync();
            await Task.Run(() => RunLongOpsAsync(MusicDatabaseService, cancellationToken), cancellationToken);
            ToolUtils.CleanupStaleCacheFiles();
            await musicBrowseViewModel.LoadPlayStateToMusicBrowsePage();
            // 许可状态必须在首次推送设置前就绪，受限判定才能作用于首推内容。
            await licenseInitialization;
            await ipcService.InitializeMusic(appViewModel.CurrentPlayingMusic);
            App.MainWindow.ShowMainPage();
            appViewModel.IsInitialized = true;
            DesktopLyricsManager.RestoreFromSettings();
            appViewModel.InitHotKeys();
            _ = ipcService.RetryStartupCorrectionsAsync();
            await CheckVersionUpdateAsync();
        }

        // 应用关闭时执行清理
        public Task StopAsync(CancellationToken cancellationToken)
        {
            App.Services.GetRequiredService<LicenseService>().Dispose();
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
                var notes = JsonSerializer.Deserialize(json, UpdateNotesJsonContext.Default.UpdateNotes);
                if (notes is null) return null;
                return AppData.SystemLanguage == "zh" ? notes.ZhCN : notes.En;
            }
            catch
            {
                return null;
            }
        }

        private static async Task RunLongOpsAsync(MusicDatabaseService db, CancellationToken ct)
        {
            using var lease = await LibraryOperationGate.EnterAsync(ct);
            await InitialFileScan.InitialScan();
            await db.LoadMusicList();
            await db.GetPlayStateAsync();
        }
    }
}
