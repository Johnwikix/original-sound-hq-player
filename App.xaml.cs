using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.VisualBasic.ApplicationServices;
using System;
using System.Diagnostics;
using System.Runtime;
using testDemo.Taskbar;
using Windows.System.UserProfile;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
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
        private Window _tempWindow = null;
        public static IServiceProvider Services { get; private set; }
        private static readonly IHost _host = Host.CreateDefaultBuilder()
             .ConfigureServices((context, services) =>
             {
                 services.AddSingleton<DatabaseInitializerService>();
                 services.AddHostedService<DatabaseInitializerService>(provider =>
                     provider.GetRequiredService<DatabaseInitializerService>());
                 // 注册导航服务为单例
                 services.AddTransient<INavigationService, NavigationService>();
                 // 注册导航服务工厂
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
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            this.UnhandledException += App_UnhandledException;
            this.InitializeComponent();
            Services = _host.Services; // 赋值给静态属性
            var systemLanguages = GlobalizationPreferences.Languages;
            if (systemLanguages[0].StartsWith("zh"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
            }
            else if (systemLanguages[0].StartsWith("es"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
            }
            else if (systemLanguages[0].StartsWith("ja"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ja";
            }
            else if (systemLanguages[0].StartsWith("ru"))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ru";
            }
            else
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en";
            }
            //Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "es";
        }
        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // 记录异常信息
            System.Diagnostics.Debug.WriteLine($"Unhandled Exception: {e.Message}");
            System.Diagnostics.Debug.WriteLine(e.Exception.StackTrace);
            // 可以选择设置 Handled 为 true 以防止应用程序崩溃
            e.Handled = true;
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
                // 记录错误信息
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
