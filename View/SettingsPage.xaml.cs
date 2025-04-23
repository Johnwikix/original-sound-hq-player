using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
            LatencyTextBox.Text = AppSettings.Latency.ToString();
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
            isInitializing = false;

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

        private void LatencyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(LatencyTextBox.Text, out int latency))
            {
                AppSettings.Latency = latency;
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
            if (sender is RadioButton radioButton)
            {
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
                {
                    switch (radioButton.Tag.ToString())
                    {
                        case "Acrylic":
                            // 设置为Acrylic背景
                            mainWindow.SystemBackdrop = new DesktopAcrylicBackdrop();
                            //if (mainWindow.current.Content is FrameworkElement rootElement)
                            //{
                            //    rootElement.RequestedTheme = ElementTheme.Light;
                            //}
                            AppSettings.AppStyle = "Acrylic";                            
                            break;

                        case "Mica":
                            // 设置为Mica背景
                            mainWindow.SystemBackdrop = new MicaBackdrop();
                            AppSettings.AppStyle = "Mica";
                            break;
                    }
                    _ = SaveSetting();
                }
            }
        }
    }
}
