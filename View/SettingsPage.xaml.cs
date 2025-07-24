using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
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
            Debug.WriteLine($"SettingsPage received parameter: {parameter}");
            await ToolUtils.RefreshDevice();
            LoadOutputDevices();
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ToolUtils.RefreshDevice();
            LoadOutputDevices();
        }

        private void LoadOutputDevices()
        {
            ViewModel.OutputDevices.Clear();
            foreach (string device in AppSettings.outputDeviceList)
            {
                ViewModel.OutputDevices.Add(device);
            }
            ViewModel.IsRealDevceChange = false;
            ViewModel.DeviceName = AppSettings.DeviceName;
        }

        //private async void ToolTip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        //{
        //    Uri uri = new Uri("https://docs.lrc.cx/docs/QuickStart/");
        //    await Launcher.LaunchUriAsync(uri);
        //}

        private void ThirdParty_Click(object sender, RoutedEventArgs e)
        {
            if (_thirdPartyDialog == null)
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
                            Text = "Description of Third-Party Component Dependencies\r\nThe Software uses the following third-party component packages, and the copyright information and usage of them are explained as follows:\r\nAudio Processing Related\r\nBunLabs.NAudio.Flac (2.0.1): Used to support decoding and encoding of FLAC format audio, extended based on the NAudio framework. Copyright belongs to BunLabs and is subject to the MIT License.\r\nCSCore (1.2.1.2): A high-performance audio processing library that provides audio capture, processing and playback functions. Copyright belongs to the CSCore team and is subject to the MIT License.\r\nCUETools.Codecs.FLAKE-Reloaded (1.0.1): An optimized implementation of the FLAC audio codec that supports lossless audio compression. Copyright belongs to the CUETools team and is subject to the LGPL-2.1 License.\r\nNAudio (2.2.1): A basic audio processing library under the .NET platform that provides functions such as audio file reading and writing, mixing, etc. Copyright belongs to Mark Heath and is subject to the Ms-PL License.\r\nNAudio.Lame (2.1.0): Integration of the LAME MP3 encoder with NAudio, supporting conversion of audio to MP3 format. Copyright belongs to the NAudio team and is subject to the MIT License.\r\nNAudio.Vorbis (1.5.0): Integration of the Vorbis encoder with NAudio, supporting OGG Vorbis format audio processing. Copyright belongs to the NAudio team and is subject to the MIT License.\r\nLordLuceus.CSCore.Ffmpeg (1.0.2): An integration library of CSCore and FFmpeg, extending support for more audio formats. Copyright belongs to LordLuceus and is subject to the MIT License.\r\n\r\nThe Original Sound HIFI Player includes components based on FFmpeg. FFmpeg is free software, licensed under the LGPL v2.1 or later versions.\r\nThe following is the copyright notice of FFmpeg:\r\n\r\nFFmpeg Copyright Notice\r\n\r\nCopyright (c) 2000-2025 FFmpeg developers\r\n\r\nFFmpeg is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation; either version 2.1 of the License, or (at your option) any later version.\r\n\r\nYou should have received a copy of the GNU Lesser General Public License; if not, please visit https://www.gnu.org/licenses/.\r\n\r\nThe source code of these components can be obtained at https://ffmpeg.org/download.html.\r\nMVVM Framework and Tools\r\nCommunityToolkit.Mvvm (8.4.0): The MVVM implementation of the Microsoft Community Toolkit, providing essential MVVM development functions such as property notification and command pattern. Copyright belongs to Microsoft and is subject to the MIT License.\r\nUI and System Integration\r\nH.NotifyIcon.WinUI (2.3.0): A system tray icon component under the WinUI platform, supporting custom tray menus and interactions. Copyright belongs to Hans-Peter Grahsl and is subject to the MIT License.\r\nMicrosoft.Graphics.Win2D (1.3.2): A high-performance 2D graphics rendering library used for graphics drawing in WinUI applications. Copyright belongs to Microsoft and is subject to the MIT License.\r\nMicrosoft.WindowsAppSDK (1.7.250401001): A set of basic functions provided by the Windows App SDK, supporting WinUI 3 application development. Copyright belongs to Microsoft and is subject to the MIT License.\r\nBasic Framework and Services\r\nMicrosoft.Extensions.Hosting (9.0.6): .NET general host framework for building extensible applications. Copyright belongs to Microsoft and is subject to the MIT License.\r\nMicrosoft.Extensions.Hosting.Abstractions (9.0.6): Abstract interface definition of the .NET host framework. Copyright belongs to Microsoft and is subject to the MIT License.\r\nMicrosoft.Windows.Compatibility (7.0.3): Provides compatibility packaging of Windows platform-specific APIs. Copyright belongs to Microsoft and is subject to the MIT License.\r\nMicrosoft.Windows.SDK.BuildTools (10.0.26100.1742): Windows SDK build tools, providing basic components required for Windows platform development. Copyright belongs to Microsoft and is subject to the MIT License.\r\nData Storage and Processing\r\nsqlite-net-pcl (1.9.172): .NET packaging of SQLite database, providing lightweight local data storage functions. Copyright belongs to Frank A. Krueger and is subject to the MIT License.\r\nSystem.Data.SqlClient (4.9.0): Microsoft SQL Server database client, providing data interaction functions with SQL Server. Copyright belongs to Microsoft and is subject to the MIT License.\r\nTagLibSharp (2.3.0): Audio file metadata (tag) processing library, supporting reading and modifying metadata in formats such as MP3 and FLAC. Copyright belongs to the TagLib# team and is subject to the LGPL-2.1 License.\r\nLogging and Diagnostics\r\nSerilog (4.3.0): A powerful logging framework that supports structured logging. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\nSerilog.Extensions.Logging (9.0.2): Integration package of Serilog and Microsoft.Extensions.Logging. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\nSerilog.Sinks.File (7.0.0): File log output plugin for Serilog. Copyright belongs to the Serilog team and is subject to the Apache-2.0 License.\r\nOther Functions\r\nMicrosoft.PinYinConverter (1.0.0): Chinese character pinyin conversion library, used for converting Chinese characters to pinyin. Copyright belongs to Microsoft and is subject to the MIT License.\r\nSystem.Formats.Asn1 (8.0.1): ASN.1 format data encoding and decoding library. Copyright belongs to Microsoft and is subject to the MIT License.\r\nSystem.Security.Cryptography.Pkcs (7.0.3): PKCS standard encryption format processing library. Copyright belongs to Microsoft and is subject to the MIT License.\r\nLicense Description\r\nAll the above third-party components are subject to their respective open-source licenses, and you can view the complete license text in their official repositories. The Software respects the copyright of all third-party components and uses these components in strict accordance with the requirements of relevant licenses."
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
    }
}
