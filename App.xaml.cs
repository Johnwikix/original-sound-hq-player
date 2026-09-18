using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.UserProfile;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Controls;
using WinUIMusicPlayer.ViewModel.Pages;
using WinUIMusicPlayer.WebService;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; set; }
        public static IServiceProvider Services { get; private set; }
        private static ILogger<App> _logger;

        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Logs");

        public static ILogger<T> GetLogger<T>()
        {
            return Services.GetRequiredService<ILogger<T>>();
        }
        private static readonly IHost _host = Host.CreateDefaultBuilder()
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                var logDirectory = LogDirectory;
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
                var logFilePath = Path.Combine(logDirectory, "WinUIMusicPlayer-.log");
                Serilog.Log.Logger = new LoggerConfiguration()
                     .MinimumLevel.Information()
                     .WriteTo.File(
                         logFilePath,
                         rollingInterval: RollingInterval.Day,
                         retainedFileCountLimit: 30,
                         outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                     .CreateLogger();
                logging.AddSerilog(Serilog.Log.Logger);
                // 设置日志级别
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("Microsoft", LogLevel.Warning);
                logging.AddFilter("System", LogLevel.Warning);
                logging.AddFilter("WinUIMusicPlayer", LogLevel.Information);
            })
             .ConfigureServices((context, services) =>
             {
                 services.AddHostedService<AppInitializerService>();
                 services.AddSingleton<AppLifecycle>();
                 services.AddSingleton<StartupCoordinator>();
                 services.AddSingleton<ShutdownCoordinator>();
                 services.AddSingleton<AudioProcessService>();
                 services.AddSingleton<PlaybackStatePersistence>();
                 services.AddSingleton<PlaybackCommands>();
                 services.AddSingleton<TrayViewModel>();
                 services.AddSingleton<LibraryWatcherService>();
                 services.AddSingleton<AudioSettingsSynchronizer>();
                 services.AddSingleton<AudioConversionViewModel>();
                 services.AddHostedService<MetadataWriteService>();
                 services.AddTransient<INavigationService, NavigationService>();
                 services.AddSingleton<INavigationServiceFactory, NavigationServiceFactory>();
                 services.AddSingleton<MainWindow>();
                 services.AddSingleton<MainPage>();
                 services.AddSingleton<PlayingDetailPage>(sp =>
                 {
                     var page = new PlayingDetailPage(sp.GetRequiredService<PlayingDetailViewModel>());
                     sp.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(page.Dispose);
                     return page;
                 });
                 services.AddSingleton<MainViewModel>();
                 services.AddSingleton<AppViewModel>();
                 services.AddSingleton<MusicBrowseViewModel>();
                 services.AddSingleton<DesktopLyricsViewModel>();
                 services.AddSingleton<AddFolderViewModel>();
                 services.AddSingleton<SettingsViewModel>();
                services.AddTransient<DspSettingsViewModel>();
                services.AddSingleton<CurvePresetService>();
                services.AddTransient<ConvolutionCurveViewModel>();
                 services.AddSingleton<AlbumViewModel>();
                 services.AddSingleton<FavouritePlayListViewModel>();
                 services.AddSingleton<ArtistViewModel>();
                 services.AddSingleton<FolderViewModel>();
                 services.AddSingleton<PlayListViewModel>();
                 services.AddSingleton<SongListViewModel>();
                 services.AddSingleton<PlayingDetailViewModel>();
                 services.AddSingleton<StatsViewModel>();
                 services.AddSingleton<PlaylistDetailViewModel>();
                 services.AddSingleton<SystemMediaControlsService>();
                 services.AddSingleton<AudioConverterService>();
                 services.AddSingleton<UsbDeviceService>();
                 services.AddSingleton<NotificationService>();
                 services.AddSingleton<LyricsRefreshService>();
                 services.AddSingleton<IpcService>();
                 services.AddSingleton<LicenseService>();
                 services.AddSingleton<BassPlayerCommandService>();
                 services.AddSingleton<PlaybackStatsService>();
                 services.AddSingleton<MusicDatabaseService>();
                 services.AddSingleton<LrcService>(sp =>
                 {
                     var service = new LrcService(sp.GetRequiredService<ILogger<LrcService>>());
                     sp.GetRequiredService<ShutdownCoordinator>().RegisterCleanup(service.Dispose);
                     return service;
                 });
             }).Build();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            // 崩溃诊断要先于任何可能失败的初始化：配置 createdump 本地转储兜底，供崩溃时由运行时拉起。
            CrashReportingService.Initialize(LogDirectory);
            this.InitializeComponent();
            Services = _host.Services;
            _logger = Services.GetRequiredService<ILogger<App>>();
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            //AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            _logger.LogInformation("应用程序初始化开始");
            var systemLanguages = GlobalizationPreferences.Languages;
            if (systemLanguages[0].StartsWith("zh"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
                AppData.SystemLanguage = "zh";
            }
            else if (systemLanguages[0].StartsWith("es"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
                AppData.SystemLanguage = "es";
            }
            else if (systemLanguages[0].StartsWith("ja"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ja";
                AppData.SystemLanguage = "ja";
            }
            else if (systemLanguages[0].StartsWith("ru"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ru";
                AppData.SystemLanguage = "ru";
            }
            else if (systemLanguages[0].StartsWith("de"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "de";
                AppData.SystemLanguage = "de";
            }
            else
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en";
                AppData.SystemLanguage = "en";
            }
            //Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var exception = e.Exception;
            var errorMessage = new StringBuilder();
            errorMessage.AppendLine($"未处理异常发生：");
            errorMessage.AppendLine($"异常类型：{exception.GetType().FullName}");
            errorMessage.AppendLine($"异常消息：{exception.Message}");
            errorMessage.AppendLine($"堆栈跟踪：{exception.StackTrace}");
            _logger.LogCritical(e.Exception, "应用程序未处理异常: {Message}", errorMessage);
            // 进程不崩溃：用 WER 报告 API 主动上报（NonCritical，静默入队、不弹 UI），
            // 报告附带异常类型/消息/栈顶参数、完整异常详情、当前日志与进程转储。
            CrashReportingService.ReportHandledException(e.Exception);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                _logger.LogCritical(exception, "应用程序域未处理异常: {Message}", exception.Message);
            }
            else
            {
                _logger.LogCritical("应用程序域未处理异常: {ExceptionObject}", e.ExceptionObject);
            }
            // 后台线程未处理异常进程必然终止：附加日志并刷盘，随后交给 WER 生成崩溃报告。
            CrashReportingService.OnFatalException();
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "任务调度器未观察到的异常: {Message}", e.Exception.Message);
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected async override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                // 检查应用程序是否已经在运行
                if (!SingleInstanceHelper.CheckSingleInstance())
                {
                    // 应用程序已在运行，尝试激活现有实例
                    SingleInstanceHelper.ActivateExistingInstance();
                    Environment.Exit(0);
                    return;
                }
                await _host.StartAsync();
                if (Services.GetRequiredService<AppLifecycle>().Phase == AppPhase.Stopping) return;
                Services.GetRequiredService<ShutdownCoordinator>().HostStarted(() => _host.StopAsync());
                Services.GetRequiredService<StartupCoordinator>().EnableInteraction();

            }
            catch (OperationCanceledException) when (Services.GetRequiredService<AppLifecycle>().Phase == AppPhase.Stopping) { }
            catch (Exception ex)
            {
                Services.GetRequiredService<AppLifecycle>().TransitionTo(AppPhase.Failed);
                _logger?.LogCritical(ex, "应用程序启动失败: {Message}", ex.Message);
                ShowStartupErrorBox(ex);
                ExitDuringStartup(1);
            }
        }

        internal static void ExitDuringStartup(int exitCode = 0) => _ = ExitApplicationAsync(exitCode);

        private static void ShowStartupErrorBox(Exception ex)
        {
            string text = $"应用程序启动失败，即将退出。\r\n\r\n" +
                          $"异常：{ex.Message}\r\n\r\n" +
                          $"详细信息已记录到日志：{LogDirectory}";
            Win32MessageBox(IntPtr.Zero, text, "启动失败", 0x10 | 0x0);
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int Win32MessageBox(IntPtr hWnd, string text, string caption, uint type);

        public static Task Current_Exit() => ExitApplicationAsync(0);

        private static async Task ExitApplicationAsync(int exitCode)
        {
            if (!await Services.GetRequiredService<ShutdownCoordinator>().ShutdownAsync()) return;
            try { Log.CloseAndFlush(); } catch { }
            SingleInstanceHelper.ReleaseMutex();
            Environment.Exit(exitCode);
        }
    }
}
