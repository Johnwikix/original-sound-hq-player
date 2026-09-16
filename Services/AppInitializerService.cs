using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
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
            var startup = Stopwatch.StartNew();
            var logger = App.GetLogger<AppInitializerService>();
            await MusicDatabaseService.Initialize();
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            await Task.WhenAll(
                MusicDatabaseService.GetEqualizerSettingsAsync(),
                MusicDatabaseService.GetSettingsAsync());
            MusicDatabaseService.LoadWindowState();
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            App.MainWindow.Activate();
            await WaitForContentLoadedAsync((FrameworkElement)App.MainWindow.Content);
            logger.LogInformation("启动窗口就绪：{ElapsedMs} ms", startup.ElapsedMilliseconds);
            var acceptance = AgreementAcceptanceStore.ForCurrentUser();
            if (!await acceptance.HasAcceptedAsync(AgreementAcceptanceStore.CurrentVersion))
            {
                var agreement = await UserAgreementViewModel.LoadAsync(acceptance);
                var result = await new View.SubView.UserAgreementDialog(agreement)
                    .ShowThemedAsync(App.MainWindow.Content.XamlRoot);
                if (result != ContentDialogResult.Primary)
                {
                    App.ExitDuringStartup();
                    return;
                }
            }
            // User reading time is not startup processing time.
            startup.Restart();
            cancellationToken.ThrowIfCancellationRequested();
            var ipcService = App.Services.GetRequiredService<IpcService>();
            var musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            App.StartAudioPlayer();
            var ipcInitialization = ipcService.InitializingAsync();
            // Independent work overlaps; no audio or online startup work runs before agreement.
            var licenseInitialization = App.Services.GetRequiredService<LicenseService>().InitializeAsync();
            var libraryInitialization = Task.Run(async () =>
            {
                await RunLongOpsAsync(MusicDatabaseService, cancellationToken);
                // Keep disk enumeration off the UI thread and before cover restoration.
                ToolUtils.CleanupStaleCacheFiles();
            }, cancellationToken);
            await Task.WhenAll(ipcInitialization, licenseInitialization, libraryInitialization);
            await musicBrowseViewModel.LoadPlayStateToMusicBrowsePage();
            // 许可状态必须在首次推送设置前就绪，受限判定才能作用于首推内容。
            await ipcService.InitializeMusic(appViewModel.CurrentPlayingMusic);
            logger.LogInformation("协议确认后核心启动完成：{ElapsedMs} ms", startup.ElapsedMilliseconds);
            // Finish startup dialogs before enabling settings, tray commands and shortcuts.
            await CheckVersionUpdateAsync();
        }

        // 由 App.OnLaunched 在 Host.StartAsync 完成后同步调用，无 await 的交互启用阶段。
        internal static void EnableInteraction()
        {
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            App.MainWindow.ShowMainPage();
            appViewModel.IsInitialized = true;
            App.Services.GetRequiredService<DesktopLyricsViewModel>().IsMainWindowForeground =
                App.MainWindow.IsForeground;
            App.MainWindow.InitializeTaskbarHelper();
            App.Services.GetRequiredService<UsbDeviceService>().StartWatching();
            App.Services.GetRequiredService<SystemMediaControlsService>().EnableControls();
            DesktopLyricsManager.RestoreFromSettings();
            appViewModel.InitHotKeys();
            _ = App.Services.GetRequiredService<IpcService>().RetryStartupCorrectionsAsync();
            App.GetLogger<AppInitializerService>().LogInformation("启动完成，主界面与系统交互已启用");
        }

        private static Task WaitForContentLoadedAsync(FrameworkElement content)
        {
            if (content.IsLoaded) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Loaded(object sender, RoutedEventArgs args)
            {
                content.Loaded -= Loaded;
                completion.SetResult();
            }
            content.Loaded += Loaded;
            return completion.Task;
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
