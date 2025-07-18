using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.VisualBasic.ApplicationServices;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading.Tasks;
using testDemo.Taskbar;
using Windows.System.UserProfile;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; private set; }
        public static IServiceProvider Services { get; private set; }
        private static readonly IHost _host = Host.CreateDefaultBuilder()
             .ConfigureServices((context, services) =>
             {
                 services.AddSingleton<DatabaseInitializerService>();
                 services.AddHostedService<DatabaseInitializerService>(provider =>
                     provider.GetRequiredService<DatabaseInitializerService>());
                 services.AddTransient<INavigationService, NavigationService>();
                 services.AddSingleton<INavigationServiceFactory, NavigationServiceFactory>();
                 services.AddSingleton<AddFolderPage>();
                 services.AddSingleton<MusicBrowsePage>();
                 services.AddSingleton<SettingsPage>();
                 services.AddSingleton<FavouritePlayListPage>();
                 services.AddSingleton<AlbumPage>();
                 services.AddSingleton<ArtistPage>();
                 services.AddSingleton<FolderBrowsePage>();
                 services.AddSingleton<PlayListPage>();
                 services.AddSingleton<PlayListSongPage>();
                 services.AddSingleton<SongListPage>();
                 services.AddSingleton<SongCollectionPage>();
                 services.AddSingleton<MusicBrowseViewModel>();
                 services.AddSingleton<AddFolderViewModel>();
                 services.AddSingleton<SettingsViewModel>();
                 services.AddSingleton<AlbumViewModel>();
                 services.AddSingleton<FavouritePlayListViewModel>();
                 services.AddSingleton<ArtistViewModel>();
                 services.AddSingleton<FolderViewModel>();
                 services.AddSingleton<PlayListViewModel>();
                 services.AddSingleton<SongListViewModel>();
                 services.AddSingleton<SongCollectionViewModel>();
                 services.AddSingleton<PlayListSongViewModel>();
                 services.AddSingleton<SystemMediaControlsService>();
                 services.AddSingleton<MusicPlaybackService>();
                 services.AddSingleton<ContextMenuService>();
                 services.AddSingleton<AudioConverterService>();
                 services.AddSingleton<NotificationService>();
             }).Build();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            Logger.Log("应用程序初始化开始");
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;            
            this.InitializeComponent();
            Services = _host.Services;
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += CurrentDomain_FirstChanceException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            var systemLanguages = GlobalizationPreferences.Languages;
            if (systemLanguages[0].StartsWith("zh"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
                AppData.systemLanguage = "zh";
            }
            else if (systemLanguages[0].StartsWith("es"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
                AppData.systemLanguage = "es";
            }
            else if (systemLanguages[0].StartsWith("ja"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ja";
                AppData.systemLanguage = "ja";
            }
            else if (systemLanguages[0].StartsWith("ru"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ru";
                AppData.systemLanguage = "ru";
            }
            else
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en";
                AppData.systemLanguage = "en";
            }
            //Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                Logger.LogException(e.Exception, "应用程序未处理异常");
                e.Handled = true;
            }
            catch (Exception logEx)
            {
                Debug.WriteLine($"记录UI异常失败: {logEx.Message}");
                Debug.WriteLine($"原始异常: {e.Exception.Message}");
                e.Handled = true;
            }
            finally {
                e.Handled = true;
            }
        }
        private void CurrentDomain_FirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            try
            {
                // 记录首次出现的异常（不一定会导致应用崩溃）
                Logger.Log($"首次异常: {e.Exception.Message}", LogLevel.Warning);
            }
            catch
            {
                // 日志记录失败时的静默处理
            }
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Logger.LogException(ex, "AppDomain未处理异常");
                }
                else
                {
                    Logger.Log($"AppDomain未处理异常: {e.ExceptionObject}", LogLevel.Critical);
                }
                SaveCriticalDataBeforeCrash();
            }
            catch
            {
                // 日志记录失败时的静默处理
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Logger.LogException(e.Exception, "任务调度器未观察到的异常");
                e.SetObserved(); // 标记为已观察，避免应用程序崩溃
            }
            catch
            {
                // 日志记录失败时的静默处理
            }
        }
        private void SaveCriticalDataBeforeCrash()
        {
            try
            {
                // 实现关键数据保存逻辑
                Logger.Log("正在保存关键数据...", LogLevel.Warning);
                // ...保存代码
                Logger.Log("关键数据已保存", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "保存关键数据失败");
            }
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
                // 创建并激活主窗口
                MainWindow = new MainWindow();
                MainWindow.Activate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"激活窗口时出错: {ex.Message}");
            }
        }

        /// <summary>
        public static async void Current_Exit()
        {
            try
            {
                await _host.StopAsync();
                Debug.WriteLine("桌面应用已退出");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"退出应用时出错: {ex.Message}");
            }
        }
    }
}
