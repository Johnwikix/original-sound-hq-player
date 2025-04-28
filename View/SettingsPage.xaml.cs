using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ServiceModel.Channels;
using System.Threading.Tasks;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        //private List<MMDevice> outputDeviceList = new List<MMDevice>();
        private bool isInitializing = true;
        private bool isDefaultComplete = true;
        private MainWindow mainWindow;
        public SettingsPage()
        {
            this.InitializeComponent();
            DateTime dateTime = DateTime.Now;
            InitializeSettings();
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.SettingLoaded += MainWindow_SettingLoaded;
            }
        }

        private void MainWindow_SettingLoaded(object? sender, EventArgs e)
        {
            LoadOutputDevices();
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is MainWindow _mainWindow)
            {
                this.mainWindow = _mainWindow;
                if (mainWindow != null)
                {
                    DateTime dateTime = DateTime.Now;
                    await mainWindow.RefreshDevice();
                }
            }
        }

        private async Task SaveSetting()
        {
            var settings = await MusicDatabaseService.GetSettings();
            SaveSettings newSettings = new SaveSettings();
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.Latency = AppSettings.Latency;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
            newSettings.DefualtEntry = AppSettings.DefualtEntry;
            newSettings.DefualtPlayList = AppSettings.DefualtPlayList;
            newSettings.LrcAPISource = AppSettings.LrcAPISource;
            newSettings.LrcAPIAuth = AppSettings.LrcAPIAuth;
            newSettings.AppStyle = AppSettings.AppStyle;
            newSettings.AppTheme = AppSettings.AppTheme;
            newSettings.isCoverCacheEnabled = AppSettings.isCoverCacheEnabled;
            newSettings.maxCoverPreLoadNum = AppSettings.maxCoverPreLoadNum;
            newSettings.isRunningBackend = AppSettings.isRunningBackend;
            newSettings.isAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
            if (settings == null)
            {
                await MusicDatabaseService.InsertSettings(newSettings);
            }
            else
            {
                await MusicDatabaseService.UpdateSettings(newSettings);
            }
        }


        private void LoadOutputDevices()
        {
            isInitializing = true;
            OutputDeviceComboBox.Items.Clear();
            List<string> outputDeviceList = AppSettings.outputDeviceList;
            foreach (var device in outputDeviceList)
            {
                OutputDeviceComboBox.Items.Add(new ComboBoxItem { Content = device });
            }
            // 设置初始的输出设备
            bool isDeviceSelected = false;
            foreach (ComboBoxItem item in OutputDeviceComboBox.Items)
            {
                if (item.Content.ToString() == AppSettings.DeviceName)
                {
                    OutputDeviceComboBox.SelectedItem = item;
                    isDeviceSelected = true;
                    break;
                }
            }
            isInitializing = false;
            if (!isDeviceSelected)
            {
                OutputDeviceComboBox.SelectedIndex = 0;
            }
        }

        private void InitializeSettings()
        {
            isInitializing = true;
            isDefaultComplete = false;
            OutputModeComboBox.SelectedIndex = 3;
            DefualtPlayListComboBox.SelectedIndex = 0;
            DefualtEntryComboBox.SelectedIndex = 0;
            isDefaultComplete = true;
            // 设置初始的输出模式
            foreach (ComboBoxItem item in OutputModeComboBox.Items)
            {
                if (item.Tag.ToString() == AppSettings.OutputMode)
                {
                    OutputModeComboBox.SelectedItem = item;
                    break;
                }
            }
            // 设置初始的缓冲区大小
            LatencyNumberBox.Value = AppSettings.Latency;
            LrcAPITextBox.Text = AppSettings.LrcAPISource;
            LrcAPIAuthTextBox.Text = AppSettings.LrcAPIAuth;
            // 设置初始的默认播放列表
            foreach (ComboBoxItem item in DefualtPlayListComboBox.Items)
            {
                DefualtPlayListComboBox.SelectedIndex = 0;
                if (item.Tag.ToString() == AppSettings.DefualtPlayList)
                {
                    DefualtPlayListComboBox.SelectedItem = item;
                    break;
                }
            }

            // 设置初始的默认条目
            foreach (ComboBoxItem item in DefualtEntryComboBox.Items)
            {
                if (item.Tag.ToString() == AppSettings.DefualtEntry)
                {
                    DefualtEntryComboBox.SelectedItem = item;
                    break;
                }
            }          

            switch (AppSettings.AppStyle)
            {
                case "Acrylic":
                    AcrylicRadioButton.IsChecked = true;
                    break;
                case "Mica":
                    MicaRadioButton.IsChecked = true;
                    break;
                default:
                    AcrylicRadioButton.IsChecked = true;
                    break;
            }
            switch (AppSettings.AppTheme)
            {
                case "Default":
                    DefaultRadioButton.IsChecked = true;
                    break;
                case "Dark":
                    DarkRadioButton.IsChecked = true;
                    break;
                case "Light":
                    LightRadioButton.IsChecked = true;
                    break;
                default:
                    DefaultRadioButton.IsChecked = true;
                    break;
            }
            MaxCoverPreLoadNumberBox.Value = AppSettings.maxCoverPreLoadNum;
            CoverCacheToggle.IsOn = AppSettings.isCoverCacheEnabled;
            AutoLyricsToggle.IsOn = AppSettings.isAutoLyricsEnabled;
            if (AppSettings.isRunningBackend) {
                RunningBackendRadioButton.IsChecked = true;
            }
            else
            {
                CloseAppRadioButton.IsChecked = true;
            }
            isInitializing = false;
           
        }

        private void OutputModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (isDefaultComplete)
                {
                    ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                    AppSettings.OutputMode = selectedItem.Tag.ToString();
                }
                if (!isInitializing)
                {
                    AppSettings.OnOutputSettingsChanged();
                    _ = SaveSetting();
                }

            }
        }

        private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (isDefaultComplete)
                {
                    ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                    AppSettings.DeviceName = selectedItem.Content.ToString();
                }
                if (!isInitializing)
                {
                    AppSettings.OnOutputSettingsChanged();
                    _ = SaveSetting();
                }
            }
        }

        private void LrcAPITextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AppSettings.LrcAPISource = LrcAPITextBox.Text;
            if (!isInitializing)
            {
                _ = SaveSetting();
            }
        }

        private void LrcAPIAuthTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            AppSettings.LrcAPIAuth = LrcAPIAuthTextBox.Text;
            if (!isInitializing)
            {
                _ = SaveSetting();
            }
        }

        private void DefualtPlayListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (isDefaultComplete)
                {
                    ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                    AppSettings.DefualtPlayList = selectedItem.Tag.ToString();
                }
                if (!isInitializing)
                {
                    _ = SaveSetting();
                }
            }
        }

        private void DefualtEntryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (isDefaultComplete)
                {
                    ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                    AppSettings.DefualtEntry = selectedItem.Tag.ToString();
                }
                if (!isInitializing)
                {
                    _ = SaveSetting();
                }
            }
        }

        private void BackdropRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            try {
                if (!isInitializing)
                {
                    if (sender is RadioButton radioButton)
                    {
                        var mainWindow = (App.MainWindow as MainWindow);
                        if (mainWindow != null)
                        {
                            switch (radioButton.Tag.ToString())
                            {
                                case "Acrylic":
                                    // 设置为Acrylic背景
                                    AppSettings.AppStyle = "Acrylic";
                                    break;

                                case "Mica":
                                    // 设置为Mica背景
                                    AppSettings.AppStyle = "Mica";
                                    break;
                            }
                            mainWindow.SetAppStyle();
                            _ = SaveSetting();
                        }
                    }
                }
            } catch(Exception ex) {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }           
        }

        private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            try {
                if (!isInitializing)
                {
                    if (sender is RadioButton radioButton)
                    {
                        var mainWindow = (App.MainWindow as MainWindow);
                        if (mainWindow != null)
                        {
                            if (mainWindow.current.Content is FrameworkElement rootElement)
                            {
                                switch (radioButton.Tag.ToString())
                                {
                                    case "Default":
                                        AppSettings.AppTheme = "Default";
                                        AppSettings.elementTheme = ElementTheme.Default;
                                        break;
                                    case "Dark":
                                        AppSettings.AppTheme = "Dark";
                                        AppSettings.elementTheme = ElementTheme.Dark;
                                        break;
                                    case "Light":
                                        AppSettings.AppTheme = "Light";
                                        AppSettings.elementTheme = ElementTheme.Light;
                                        break;
                                    default:
                                        AppSettings.AppTheme = "Default";
                                        AppSettings.elementTheme = ElementTheme.Default;
                                        break;
                                }
                                mainWindow.SetAppTheme();
                                _ = SaveSetting();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { 
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
                      
        }

        private void CoverCacheToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isInitializing)
            {
                AppSettings.isCoverCacheEnabled = CoverCacheToggle.IsOn;
                _ = SaveSetting();
            }            
        }

        private void MaxCoverPreLoadNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!isInitializing)
            {
                AppSettings.maxCoverPreLoadNum = (int)args.NewValue;
                _ = SaveSetting();
            }
        }

        private async void ToolTip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            Uri uri = new Uri("https://docs.lrc.cx/docs/QuickStart/");
            await Launcher.LaunchUriAsync(uri);
        }

        private void LatencyNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {           
            if (!isInitializing)
            {
                AppSettings.Latency = (int)LatencyNumberBox.Value;
                AppSettings.OnOutputSettingsChanged();
                _ = SaveSetting();
            }
        }

        private void ClosedRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!isInitializing)
                {
                    if (sender is RadioButton radioButton)
                    {
                        switch (radioButton.Tag.ToString())
                        {
                            case "Closed":
                                AppSettings.isRunningBackend = false;
                                break;

                            case "RunningBackend":
                                AppSettings.isRunningBackend = true;
                                break;
                        }
                        _ = SaveSetting();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void AutoLyricsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!isInitializing)
            {
                AppSettings.isAutoLyricsEnabled = AutoLyricsToggle.IsOn;
                _ = SaveSetting();
            }
        }
    }
}
