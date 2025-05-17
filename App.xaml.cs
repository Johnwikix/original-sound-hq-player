using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using Windows.System.UserProfile;
using WinUIMusicPlayer.Helper;

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
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
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
            else if (systemLanguages[0].StartsWith("ru")) {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ru";
            }
            else
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en";
            }
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ja";
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                // 检查应用程序是否已经在运行
                if (!SingleInstanceHelper.CheckSingleInstance())
                {
                    // 应用程序已在运行，尝试激活现有实例
                    //SingleInstanceHelper.ActivateExistingInstance();
                    Environment.Exit(0);
                    return;
                }

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

    }
}
