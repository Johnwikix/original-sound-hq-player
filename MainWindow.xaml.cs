using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.View;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Utils;
using Windows.ApplicationModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private AppWindow m_AppWindow;
        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            m_AppWindow = GetAppWindowForCurrentWindow(this);
            string iconPath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets/icon.ico");
            m_AppWindow.SetIcon(iconPath);
            SetTitleBarColors();
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        private AppWindow GetAppWindowForCurrentWindow(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        private bool SetTitleBarColors()
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                AppWindowTitleBar m_TitleBar = m_AppWindow.TitleBar;

                UISettings settings = new UISettings();
                var color = settings.GetColorValue(UIColorType.Background);
                // Set active window colors.
                // Note: No effect when app is running on Windows 10
                // because color customization is not supported.
                m_TitleBar.ForegroundColor = ToolUtils.InvertColor(color);
                m_TitleBar.BackgroundColor = color;
                m_TitleBar.ButtonForegroundColor = ToolUtils.InvertColor(color);
                m_TitleBar.ButtonBackgroundColor = color;
                m_TitleBar.ButtonHoverForegroundColor = ToolUtils.ConvertToColorOffset(color, (byte)20);
                m_TitleBar.ButtonHoverBackgroundColor = ToolUtils.ConvertToColorOffset(ToolUtils.InvertColor(color), (byte)20);
                m_TitleBar.ButtonPressedForegroundColor = ToolUtils.ConvertToColorOffset(color, (byte)40);
                m_TitleBar.ButtonPressedBackgroundColor = ToolUtils.ConvertToColorOffset(ToolUtils.InvertColor(color), (byte)40);
                // Set inactive window colors.
                // Note: No effect when app is running on Windows 10
                // because color customization is not supported.
                m_TitleBar.InactiveForegroundColor = ToolUtils.ConvertToColorOffset(ToolUtils.InvertColor(color), (byte)30);
                m_TitleBar.InactiveBackgroundColor = ToolUtils.ConvertToColorOffset(color, (byte)30);
                m_TitleBar.ButtonInactiveForegroundColor = ToolUtils.ConvertToColorOffset(ToolUtils.InvertColor(color), (byte)30);
                m_TitleBar.ButtonInactiveBackgroundColor = ToolUtils.ConvertToColorOffset(color, (byte)30); 
                return true;
            }
            return false;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 移除事件处理程序，避免重复触发
            this.Activated -= MainWindow_Activated;
            // 初始导航到 AddFolder 页面
            NavigateToPage(typeof(AddFolderPage));
        }
        private void NavigateToPage(Type pageType)
        {
            ContentFrame.Navigate(pageType);
        }


        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                NavigateToPage(typeof(SettingsPage));
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        NavigateToPage(typeof(AddFolderPage));
                        break;
                    case "MusicBrowse":
                        NavigateToPage(typeof(MusicBrowsePage));
                        break;
                }
            }
        }
    }
}
