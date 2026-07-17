using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.System.UserProfile;
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
        public static ILogger<T> GetLogger<T>()
        {
            return Services.GetRequiredService<ILogger<T>>();
        }
        private static readonly IHost _host = Host.CreateDefaultBuilder()
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Logs");
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
                 services.AddTransient<INavigationService, NavigationService>();
                 services.AddSingleton<INavigationServiceFactory, NavigationServiceFactory>();
                 services.AddSingleton<MainWindow>();
                 services.AddSingleton<MainPage>();
                 services.AddSingleton<PlayingDetailPage>();
                 services.AddSingleton<MainViewModel>();
                 services.AddSingleton<AppViewModel>();
                 services.AddSingleton<MusicBrowseViewModel>();
                 services.AddSingleton<AddFolderViewModel>();
                 services.AddSingleton<SettingsViewModel>();
                 services.AddSingleton<AlbumViewModel>();
                 services.AddSingleton<FavouritePlayListViewModel>();
                 services.AddSingleton<ArtistViewModel>();
                 services.AddSingleton<FolderViewModel>();
                 services.AddSingleton<PlayListViewModel>();
                 services.AddSingleton<SongListViewModel>();
                 services.AddSingleton<PlayingDetailViewModel>();
                 services.AddSingleton<MusicGroupDetailViewModel>();
                 services.AddSingleton<PlaylistDetailViewModel>();
                 services.AddSingleton<SystemMediaControlsService>();
                 services.AddSingleton<AudioConverterService>();
                 services.AddSingleton<NotificationService>();
                 services.AddSingleton<LyricsRefreshService>();
                 services.AddSingleton<IpcService>();
                 services.AddSingleton<BassPlayerCommandService>();
                 services.AddSingleton<MusicDatabaseService>();
                 services.AddSingleton<LrcService>();
             }).Build();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
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

        private void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            var exception = e.Exception;
            var errorMessage = new StringBuilder();
            errorMessage.AppendLine($"首次机会异常");
            errorMessage.AppendLine($"异常类型：{exception.GetType().FullName}");
            errorMessage.AppendLine($"异常消息：{exception.Message}");
            errorMessage.AppendLine($"堆栈跟踪：{exception.StackTrace}");
            _logger.LogError(e.Exception, "首次机会异常: {Message}", errorMessage);
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var exception = e.Exception;
            var errorMessage = new StringBuilder();
            errorMessage.AppendLine($"未处理异常发生：");
            errorMessage.AppendLine($"异常类型：{exception.GetType().FullName}");
            errorMessage.AppendLine($"异常消息：{exception.Message}");
            errorMessage.AppendLine($"堆栈跟踪：{exception.StackTrace}");
            _logger.LogError(e.Exception, "应用程序未处理异常: {Message}", errorMessage);
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
                Process.StartAndForget(new ProcessStartInfo
                {
                    FileName = "BassPlayerSharp.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                await _host.StartAsync();

            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "应用程序启动时出错: {Message}", ex.Message);
            }
        }

        /// <summary>
        public static async Task Current_Exit()
        {
            try
            {
                await SavePlayStateAsync();
                Services.GetRequiredService<BassPlayerCommandService>().MusicEnd();
                MainWindow.Hide();
                await _host.StopAsync();
                Services.GetRequiredService<LrcService>().Dispose();
                Services.GetRequiredService<PlayingDetailPage>().Dispose();
                Services.GetRequiredService<AppViewModel>().Dispose();
                CoverLoadQueue.Shutdown(TimeSpan.FromSeconds(3));
                //_host.Dispose();
                MainWindow.Dispose();
                _logger?.LogInformation("应用程序退出完成");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "退出应用时出错: {Message}", ex.Message);
            }
            finally
            {
                //Current.Exit();
                Environment.Exit(0);
            }
        }

        private static async Task SavePlayStateAsync()
        {
            try
            {
                var db = Services.GetService<MusicDatabaseService>();
                var appvm = Services.GetService<AppViewModel>();
                if (db == null || appvm == null || MainWindow == null) return;
                if (!MainWindow.HasTrackedBounds) return;

                var (x, y, w, h) = MainWindow.TrackedBounds;
                var playState = new SavePlayState
                {
                    PlayMode = appvm.CurrentPlayMode,
                    LastPlayedMusicId = appvm.CurrentPlayingMusic?.Id,
                    Volume = appvm.Volume,
                    SortOrder = appvm.SelectedSortOption?.Tag?.ToString() ?? "DefaultOrder",
                    HasWindowBounds = true,
                    WindowX = x,
                    WindowY = y,
                    WindowWidth = w,
                    WindowHeight = h,
                    IsMaximized = MainWindow.IsCurrentlyMaximized
                };
                await db.SavePlayStateAsync(playState, appvm.SequentialPlayingList);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存播放状态失败: {Message}", ex.Message);
            }
        }
    }
}
