using DevWinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page, INavigatable
    {
        private ContentDialog? _thirdPartyDialog;
        public SettingsViewModel ViewModel { get; }
        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
        }

        public async void ReceiveNavigationParameter(object parameter)
        {
            LoadOutputDevices();
        }

        private void LoadOutputDevices()
        {
            ViewModel.AppViewModel.IsRealDevceChange = false;
            ViewModel.InitializeWasapiDevice();
        }

        private void ThirdParty_Click(object sender, RoutedEventArgs e)
        {
            if (_thirdPartyDialog is null)
            {
                _thirdPartyDialog = new ContentDialog
                {
                    Title = ToolUtils.GetString("ThirdPartyComponentsText"),
                    Content = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = "Description of Third-Party Component Dependencies\r\nThe Software uses the following third-party component packages, and the copyright information and usage of them are explained as follows:\r\n\r\n## Audio Processing Related\r\n- Un4seen.Bass (2.4.16): A high-performance cross-platform audio library that provides core capabilities such as audio playback, recording, streaming, and real-time effects processing. Copyright belongs to un4seen developments and is subject to the Bass License.\r\n- ManagedBass (3.1.0): A .NET wrapper for the Un4seen.Bass library, enabling .NET applications to access and utilize the full functionality of the Bass library through managed code, simplifying integration in .NET development environments. Copyright belongs to the ManagedBass developers and is subject to the MIT License.\r\n\r\n## MVVM Framework and Tools\r\n- CommunityToolkit.Mvvm (8.4.0): The MVVM implementation of the Microsoft Community Toolkit, providing essential MVVM development functions such as property notification and command pattern. Copyright belongs to Microsoft and is subject to the MIT License.\r\n\r\n## UI and System Integration\r\n- H.NotifyIcon.WinUI (2.3.0): A system tray icon component under the WinUI platform, supporting custom tray menus and interactions. Copyright belongs to Hans-Peter Grahsl and is subject to the MIT License.\r\n- Microsoft.Graphics.Win2D (1.3.2): A high-performance 2D graphics rendering library used for graphics drawing in WinUI applications. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- Microsoft.WindowsAppSDK (1.7.250401001): A set of basic functions provided by the Windows App SDK, supporting WinUI 3 application development. Copyright belongs to Microsoft and is subject to the MIT License.\r\n\r\n## Basic Framework and Services\r\n- Microsoft.Extensions.Hosting (9.0.6): .NET general host framework for building extensible applications. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- Microsoft.Extensions.Hosting.Abstractions (9.0.6): Abstract interface definition of the .NET host framework. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- Microsoft.Windows.Compatibility (7.0.3): Provides compatibility packaging of Windows platform-specific APIs. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- Microsoft.Windows.SDK.BuildTools (10.0.26100.1742): Windows SDK build tools, providing basic components required for Windows platform development. Copyright belongs to Microsoft and is subject to the MIT License.\r\n\r\n## Data Storage and Processing\r\n- sqlite-net-pcl (1.9.172): .NET packaging of SQLite database, providing lightweight local data storage functions. Copyright belongs to Frank A. Krueger and is subject to the MIT License.\r\n- System.Data.SqlClient (4.9.0): Microsoft SQL Server database client, providing data interaction functions with SQL Server. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- TagLibSharp (2.3.0): Audio file metadata (tag) processing library, supporting reading and modifying metadata in formats such as MP3 and FLAC. Copyright belongs to the TagLib# team and is subject to the LGPL-2.1 License.\r\n\r\n## Logging and Diagnostics\r\n- Serilog (4.3.0): A powerful logging framework that supports structured logging. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\n- Serilog.Extensions.Logging (9.0.2): Integration package of Serilog and Microsoft.Extensions.Logging. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\n- Serilog.Sinks.File (7.0.0): File log output plugin for Serilog. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\n\r\n## Other Functions\r\n- Microsoft.PinYinConverter (1.0.0): Chinese character pinyin conversion library, used for converting Chinese characters to pinyin. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- System.Formats.Asn1 (8.0.1): ASN.1 format data encoding and decoding library. Copyright belongs to Microsoft and is subject to the MIT License.\r\n- System.Security.Cryptography.Pkcs (7.0.3): PKCS standard encryption format processing library. Copyright belongs to Microsoft and is subject to the MIT License.\r\n\r\n## License Description\r\nAll the above third-party components are subject to their respective open-source licenses, and you can view the complete license text in their official repositories. The Software respects the copyright of all third-party components and uses these components in strict accordance with the requirements of relevant licenses."
                        }
                    },
                    CloseButtonText = ToolUtils.GetString("CloseButton"),
                    XamlRoot = this.XamlRoot
                };
                _thirdPartyDialog.RequestedTheme = AppSettings.elementTheme;
            }
            _thirdPartyDialog?.ShowAsync();
        }

        private async void LrcAPISource_Click(object sender, RoutedEventArgs e)
        {
            Uri uri = new Uri("https://docs.lrc.cx/docs/QuickStart/");
            await Launcher.LaunchUriAsync(uri);
        }

        private void SpectrumVisualization_Click(object sender, RoutedEventArgs e)
        {
            string storeUri = "spectrumvisualization:";
            LauncherOptions options = new LauncherOptions
            {
                FallbackUri = new Uri("ms-windows-store://pdp/?ProductId=9PL2DSHJ79W7")
            };
            _ = Launcher.LaunchUriAsync(new Uri(storeUri), options);
        }

        private void AutoScrollHover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = true;
            }
        }

        private void AutoScrollHover_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }

        private void AutoScrollHover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is AutoScrollView autoScrollView)
            {
                autoScrollView.IsPlaying = false;
            }
        }
    }
}
