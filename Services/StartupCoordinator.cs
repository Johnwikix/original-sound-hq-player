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
using WinUIEx;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class StartupCoordinator
    {
        private readonly AppViewModel AppViewModel;
        private readonly MusicDatabaseService MusicDatabaseService;
        public StartupCoordinator(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            MusicDatabaseService = musicDatabaseService;
        }
        // 应用启动时执行初始化
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, App.Services.GetRequiredService<AppLifecycle>().StoppingToken);
            cancellationToken = startupCancellation.Token;
            var startup = Stopwatch.StartNew();
            var logger = App.GetLogger<StartupCoordinator>();
            await MusicDatabaseService.Initialize();
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            await Task.WhenAll(
                MusicDatabaseService.GetEqualizerSettingsAsync(),
                MusicDatabaseService.GetSettingsAsync());
            // ThemeType 初始即为 Default，恢复同值不会触发 setter；创建视图前显式解析实际主题。
            appViewModel.UpdateCover();
            MusicDatabaseService.LoadWindowState();
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            var shutdown = App.Services.GetRequiredService<ShutdownCoordinator>();
            shutdown.RegisterCleanup(App.MainWindow.Dispose);
            shutdown.RegisterCleanup(appViewModel.Dispose);
            App.MainWindow.InitializeTray();
            shutdown.RegisterCleanup(App.Services.GetRequiredService<PlaybackCommands>().Dispose);
            App.MainWindow.Activate();
            await WaitForContentLoadedAsync((FrameworkElement)App.MainWindow.Content);
            logger.LogInformation("启动窗口就绪：{ElapsedMs} ms", startup.ElapsedMilliseconds);
            var acceptance = AgreementAcceptanceStore.ForCurrentUser();
            if (!await acceptance.HasAcceptedAsync(AgreementAcceptanceStore.CurrentVersion))
            {
                App.Services.GetRequiredService<AppLifecycle>().TransitionTo(AppPhase.WaitingForAgreement);
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
            App.Services.GetRequiredService<AppLifecycle>().TransitionTo(AppPhase.Initializing);
            startup.Restart();
            cancellationToken.ThrowIfCancellationRequested();
            var ipcService = App.Services.GetRequiredService<IpcService>();
            var musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            shutdown.RegisterCleanup(musicBrowseViewModel.Dispose);
            var audio = App.Services.GetRequiredService<AudioProcessService>();
            shutdown.RegisterCleanup(audio.Dispose);
            audio.Start();
            shutdown.RegisterCleanup(ipcService.Dispose);
            var commands = App.Services.GetRequiredService<PlaybackCommands>();
            var media = App.Services.GetRequiredService<SystemMediaControlsService>();
            shutdown.RegisterCleanup(media.Dispose);
            media.Initialize(commands);
            var settingsSync = App.Services.GetRequiredService<AudioSettingsSynchronizer>();
            shutdown.RegisterCleanup(settingsSync.Dispose);
            settingsSync.Start();
            var watcher = App.Services.GetRequiredService<LibraryWatcherService>();
            shutdown.RegisterCleanup(watcher.StopAsync);
            await watcher.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            shutdown.RegisterCleanup(App.Services.GetRequiredService<LicenseService>().Dispose);
            var stateStore = App.Services.GetRequiredService<PlaybackStatePersistence>();
            var statistics = App.Services.GetRequiredService<PlaybackStatsService>();
            var player = App.Services.GetRequiredService<BassPlayerCommandService>();
            shutdown.RegisterSave(() => stateStore.SaveAsync(App.MainWindow));
            shutdown.RegisterSave(statistics.FlushSessionAsync);
            shutdown.RegisterSave(() => { player.MusicEnd(); App.MainWindow.Hide(); return Task.CompletedTask; });
            shutdown.RegisterCleanup(() => CoverLoadQueue.Shutdown(TimeSpan.FromSeconds(3)));
            var ipcInitialization = ipcService.InitializingAsync();
            // Independent work overlaps; no audio or online startup work runs before agreement.
            var licenseInitialization = App.Services.GetRequiredService<LicenseService>().InitializeAsync();
            var libraryInitialization = Task.Run(async () =>
            {
                await RunLongOpsAsync(MusicDatabaseService, cancellationToken);
                // Keep disk enumeration off the UI thread and before cover restoration.
                ToolUtils.CleanupStaleCacheFiles();
            }, cancellationToken);
            await Task.WhenAll(ipcInitialization, licenseInitialization, libraryInitialization).WaitAsync(cancellationToken);
            await musicBrowseViewModel.LoadPlayStateToMusicBrowsePage();
            // 许可状态必须在首次推送设置前就绪，受限判定才能作用于首推内容。
            await ipcService.InitializeMusic(appViewModel.CurrentPlayingMusic);
            logger.LogInformation("协议确认后核心启动完成：{ElapsedMs} ms", startup.ElapsedMilliseconds);
            // Finish startup dialogs before enabling settings, tray commands and shortcuts.
            await CheckVersionUpdateAsync();
        }

        // 由 App.OnLaunched 在 Host.StartAsync 完成后同步调用，无 await 的交互启用阶段。
        internal void EnableInteraction()
        {
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            App.MainWindow.ShowMainPage();
            App.Services.GetRequiredService<AppLifecycle>().TransitionTo(AppPhase.Ready);
            App.Services.GetRequiredService<DesktopLyricsViewModel>().IsMainWindowForeground =
                App.MainWindow.IsForeground;
            App.MainWindow.InitializeTaskbarHelper();
            var usb = App.Services.GetRequiredService<UsbDeviceService>();
            App.Services.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(usb.StopWatching);
            usb.StartWatching();
            App.Services.GetRequiredService<SystemMediaControlsService>().EnableControls();
            App.Services.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(DesktopLyricsManager.Shutdown);
            DesktopLyricsManager.RestoreFromSettings();
            appViewModel.InitHotKeys();
            _ = App.Services.GetRequiredService<IpcService>().RetryStartupCorrectionsAsync();
            App.GetLogger<StartupCoordinator>().LogInformation("启动完成，主界面与系统交互已启用");
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
                var logger = App.GetLogger<StartupCoordinator>();
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
