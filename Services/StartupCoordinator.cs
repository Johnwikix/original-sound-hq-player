using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.WinUI;
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
        private Task? _libraryScan;
        private Task? _engineInitialization;
        private volatile bool _engineInitialized;
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
            // 音频进程与 IPC 连接最先启动（先于用户协议）：服务端首个动作即创建共享内存，
            // 连接等待与数据库初始化、协议阅读并行；连接完成不阻塞主界面，进度由标题栏指示。
            var shutdown = App.Services.GetRequiredService<ShutdownCoordinator>();
            var lifecycle = App.Services.GetRequiredService<AppLifecycle>();
            var ipcService = App.Services.GetRequiredService<IpcService>();
            var audio = App.Services.GetRequiredService<AudioProcessService>();
            shutdown.RegisterCleanup(audio.Dispose);
            audio.Start();
            shutdown.RegisterCleanup(ipcService.Dispose);
            AppViewModel.Progress.Begin(ProgressCenter.Keys.IpcConnecting, ToolUtils.GetString("ProgressConnecting"));
            var ipcInitialization = ipcService.InitializingAsync();
            await MusicDatabaseService.Initialize();
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            await Task.WhenAll(
                MusicDatabaseService.GetEqualizerSettingsAsync(),
                MusicDatabaseService.GetSettingsAsync());
            // ThemeType 初始即为 Default，恢复同值不会触发 setter；创建视图前显式解析实际主题。
            appViewModel.UpdateCover();
            MusicDatabaseService.LoadWindowState();
            App.MainWindow = App.Services.GetRequiredService<MainWindow>();
            shutdown.RegisterCleanup(App.MainWindow.Dispose);
            shutdown.RegisterCleanup(appViewModel.Dispose);
            App.MainWindow.InitializeTray();
            shutdown.RegisterCleanup(App.Services.GetRequiredService<PlaybackCommands>().Dispose);
            shutdown.RegisterCleanup(App.Services.GetRequiredService<OneShotPlaybackService>().Dispose);
            App.MainWindow.Activate();
            await WaitForContentLoadedAsync((FrameworkElement)App.MainWindow.Content);
            logger.LogInformation("启动窗口就绪：{ElapsedMs} ms", startup.ElapsedMilliseconds);
            // 主界面立即显示：无 Loading 过渡层，缓存库/播放状态/引擎就绪后逐步流入。
            // 首装时用户协议弹窗覆盖在主界面之上。
            App.MainWindow.ShowMainPage();
            var acceptance = AgreementAcceptanceStore.ForCurrentUser();
            if (!await acceptance.HasAcceptedAsync(AgreementAcceptanceStore.CurrentVersion))
            {
                lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
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
            lifecycle.TransitionTo(AppPhase.Initializing);
            startup.Restart();
            cancellationToken.ThrowIfCancellationRequested();
            var musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            shutdown.RegisterCleanup(musicBrowseViewModel.Dispose);
            var commands = App.Services.GetRequiredService<PlaybackCommands>();
            var media = App.Services.GetRequiredService<SystemMediaControlsService>();
            shutdown.RegisterCleanup(media.Dispose);
            media.Initialize(commands);
            var settingsSync = App.Services.GetRequiredService<AudioSettingsSynchronizer>();
            shutdown.RegisterCleanup(settingsSync.Dispose);
            settingsSync.Start();
            shutdown.RegisterCleanup(App.Services.GetRequiredService<LicenseService>().Dispose);
            var stateStore = App.Services.GetRequiredService<PlaybackStatePersistence>();
            var statistics = App.Services.GetRequiredService<PlaybackStatsService>();
            var player = App.Services.GetRequiredService<BassPlayerCommandService>();
            shutdown.RegisterSave(() => stateStore.SaveAsync(App.MainWindow));
            shutdown.RegisterSave(statistics.FlushSessionAsync);
            shutdown.RegisterSave(() => { player.MusicEnd(); App.MainWindow.Hide(); return Task.CompletedTask; });
            shutdown.RegisterCleanup(() => CoverLoadQueue.Shutdown(TimeSpan.FromSeconds(3)));
            var licenseInitialization = App.Services.GetRequiredService<LicenseService>().InitializeAsync();
            AppViewModel.Progress.Begin(ProgressCenter.Keys.LibraryLoading, ToolUtils.GetString("ProgressLoadingLibrary"));
            var libraryInitialization = Task.Run(async () =>
            {
                await LoadCachedLibraryAsync(MusicDatabaseService, cancellationToken);
                // Keep disk enumeration off the UI thread and before cover restoration.
                ToolUtils.CleanupStaleCacheFiles();
            }, cancellationToken);
            // 引擎轨：连接完成后推送首曲与设置，与许可/缓存库并行，不阻塞主界面显示；
            // 完成前播放入口保持置灰（IsPlaybackEngineReady）。
            _engineInitialization = Task.Run(() => InitializePlaybackEngineAsync(
                ipcInitialization, licenseInitialization, libraryInitialization, cancellationToken));
            shutdown.RegisterCleanup(() => _engineInitialization ?? Task.CompletedTask);
            await Task.WhenAll(licenseInitialization, libraryInitialization).WaitAsync(cancellationToken);
            AppViewModel.Progress.Complete(ProgressCenter.Keys.LibraryLoading);
            await musicBrowseViewModel.LoadPlayStateToMusicBrowsePage();
            // 缓存和播放状态恢复完成后才允许监视器发布音乐库变化。
            var watcher = App.Services.GetRequiredService<LibraryWatcherService>();
            shutdown.RegisterCleanup(watcher.StopAsync);
            await watcher.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("协议确认后核心启动完成：{ElapsedMs} ms", startup.ElapsedMilliseconds);
        }

        /// <summary>等待 IPC 连接、许可与缓存库（播放状态来源）后推送首曲；后台线程运行，VM 更新回 UI 线程。</summary>
        private async Task InitializePlaybackEngineAsync(
            Task ipcInitialization, Task licenseInitialization, Task libraryInitialization,
            CancellationToken cancellationToken)
        {
            var ipcService = App.Services.GetRequiredService<IpcService>();
            try
            {
                await Task.WhenAll(ipcInitialization, licenseInitialization, libraryInitialization).WaitAsync(cancellationToken);
                // 一次性外部文件已解析完成时，首推直接加载它，省去先加载恢复曲再切换的一次开销；
                // 解析未完成则照常推恢复曲，外部文件在引擎就绪后接替播放（OneShotPlaybackService 事件驱动派发）。
                var initialMusic = AppViewModel.CurrentPlayingMusic;
                if (App.Services.GetRequiredService<OneShotPlaybackService>().TryGetResolvedMusic() is { } oneShotMusic)
                    initialMusic = oneShotMusic;
                await ipcService.InitializeMusic(initialMusic);
                _engineInitialized = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // 连接失败时 InitializingAsync 已触发退出；此处仅记录，播放入口保持置灰。
                App.GetLogger<StartupCoordinator>().LogError(ex, "播放引擎初始化失败，播放入口保持不可用");
            }
            if (cancellationToken.IsCancellationRequested) return;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                // 连接阶段结束即移除进度层任务条目；入口可用性由 UpdatePlaybackEngineReady 汇总判定。
                AppViewModel.Progress.Complete(ProgressCenter.Keys.IpcConnecting);
                UpdatePlaybackEngineReady();
            });
            // 校正同步在首推中失败时才需要重试；此时 IPC 已连接，重试可达。
            if (_engineInitialized && ipcService.CorrectionSyncFailed)
                _ = ipcService.RetryStartupCorrectionsAsync();
        }

        /// <summary>播放引擎就绪 = 生命周期 Ready 且 IPC 已连接且首曲推送完成；主界面启用与引擎轨完成两处刷新。</summary>
        private void UpdatePlaybackEngineReady()
        {
            AppViewModel.IsPlaybackEngineReady =
                _engineInitialized && App.Services.GetRequiredService<IpcService>().IsConnected &&
                App.Services.GetRequiredService<AppLifecycle>().IsReady;
        }

        // 由 App.OnLaunched 在 Host.StartAsync 完成后同步调用，无 await 的交互启用阶段。
        internal void EnableInteraction()
        {
            var appViewModel = App.Services.GetRequiredService<AppViewModel>();
            // 主界面已在启动早期注入（StartAsync 内），此处只迁移生命周期状态。
            App.Services.GetRequiredService<AppLifecycle>().TransitionTo(AppPhase.Ready);
            // Ready 不再隐含 IPC 已连接：引擎初始化未完成时播放入口保持置灰，完成时由引擎轨再次刷新。
            UpdatePlaybackEngineReady();
            App.Services.GetRequiredService<DesktopLyricsViewModel>().IsMainWindowShown =
                App.MainWindow.Visible;
            App.MainWindow.InitializeTaskbarHelper();
            var usb = App.Services.GetRequiredService<UsbDeviceService>();
            App.Services.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(usb.StopWatching);
            usb.StartWatching();
            App.Services.GetRequiredService<SystemMediaControlsService>().EnableControls();
            App.Services.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(DesktopLyricsManager.Shutdown);
            DesktopLyricsManager.RestoreFromSettings();
            appViewModel.InitHotKeys();
            StartLibraryScan();
            // 更新日志改为后台弹出：不阻塞交互启用，关闭后才记录已读版本。
            _ = CheckVersionUpdateAsync();
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

        private void StartLibraryScan()
        {
            if (_libraryScan is not null) return;
            var token = App.Services.GetRequiredService<AppLifecycle>().StoppingToken;
            // 退出先取消生命周期 token，再等待真实扫描结束，之后才释放视图和音频依赖。
            App.Services.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(
                () => _libraryScan ?? Task.CompletedTask);
            AppViewModel.Progress.Begin(ProgressCenter.Keys.LibraryScanning, ToolUtils.GetString("ProgressScanning"));
            var progress = new Progress<int>(percent =>
            {
                if (!token.IsCancellationRequested)
                    AppViewModel.Progress.Report(ProgressCenter.Keys.LibraryScanning, percent);
            });
            _libraryScan = Task.Run(() => ScanLibraryAsync(token, progress));
        }

        private async Task ScanLibraryAsync(CancellationToken token, IProgress<int> progress)
        {
            var logger = App.GetLogger<StartupCoordinator>();
            try
            {
                using var lease = await LibraryOperationGate.EnterAsync(token);
                bool changed = false;
                try
                {
                    changed = await InitialFileScan.InitialScan(token, progress);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 中途失败无法排除已提交的批次，按有变更处理保守刷新。
                    changed = true;
                    throw;
                }
                finally
                {
                    // 无变更不刷新：避免启动时列表在数据未变的情况下被 Reset 二次重置（闪两次）。
                    // 失败也可能已提交部分批次；只刷新库，不重放启动状态或重建用户的播放队列。
                    if (!token.IsCancellationRequested && changed)
                        await AppViewModel.RefreshSongsSourceAsync(token);
                }
                logger.LogInformation("启动后台音乐扫描完成，{Result}", changed ? "数据库有变更" : "数据库无变更");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "启动后台音乐扫描失败，保留已加载的音乐库");
            }
            finally
            {
                // ProgressCenter 自带 UI 线程转派，后台线程可直接收尾。
                AppViewModel.Progress.Complete(ProgressCenter.Keys.LibraryScanning);
            }
        }

        private static async Task LoadCachedLibraryAsync(MusicDatabaseService db, CancellationToken ct)
        {
            using var lease = await LibraryOperationGate.EnterAsync(ct);
            await db.LoadMusicList();
            ct.ThrowIfCancellationRequested();
            await db.GetPlayStateAsync();
        }
    }
}
