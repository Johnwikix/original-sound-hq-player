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
using Microsoft.UI.Composition.SystemBackdrops;

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
            SystemBackdrop = SystemBackdrop = new MicaBackdrop()
            { Kind = MicaKind.Base };
            ExtendsContentIntoTitleBar = true; // 将页面扩展到标题栏中（隐藏了传统的标题栏）
            SetTitleBar(AppTitleBar);
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
