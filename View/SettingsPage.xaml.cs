using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NAudio.CoreAudioApi;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private List<MMDevice> outputDeviceList = new List<MMDevice>();
        private bool isInitializing = true;
        private bool isDefaultComplete = true;
        private MainWindow mainWindow;
        public SettingsPage()
        {
            this.InitializeComponent();
            DateTime dateTime = DateTime.Now;
            InitializeDatabase();
            InitializeSettings();
            mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.SettingLoaded += MainWindow_SettingLoaded;
            }
        }

        private void MainWindow_SettingLoaded(object? sender, MMDeviceCollection devices)
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

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<SaveSettings>();
        }

        private async Task SaveSetting()
        {
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            SaveSettings newSettings = new SaveSettings();
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.Latency = AppSettings.Latency;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
            newSettings.DefualtEntry = AppSettings.DefualtEntry;
            newSettings.DefualtPlayList = AppSettings.DefualtPlayList;
            if (settings == null)
            {
                await dbConnection.InsertAsync(newSettings);
            }
            else
            {
                await dbConnection.UpdateAsync(newSettings);
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
            if (!isDeviceSelected) {
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
                if (item.Content.ToString() == AppSettings.OutputMode)
                {
                    OutputModeComboBox.SelectedItem = item;
                    break;
                }
            }            
            // 设置初始的缓冲区大小
            LatencyTextBox.Text = AppSettings.Latency.ToString();
            // 设置初始的默认播放列表
            foreach (ComboBoxItem item in DefualtPlayListComboBox.Items)
            {
                DefualtPlayListComboBox.SelectedIndex = 0;
                if (item.Content.ToString() == AppSettings.DefualtPlayList)
                {
                    DefualtPlayListComboBox.SelectedItem = item;
                    break;
                }
            }
            
            // 设置初始的默认条目
            foreach (ComboBoxItem item in DefualtEntryComboBox.Items)
            {                
                if (item.Content.ToString() == AppSettings.DefualtEntry)
                {
                    DefualtEntryComboBox.SelectedItem = item;
                    break;
                }
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
                    AppSettings.OutputMode = selectedItem.Content.ToString();
                }                
                if (!isInitializing)
                {
                    AppSettings.OnOutputSettingsChanged();
                    SaveSetting();
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
                    SaveSetting();
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
                    SaveSetting();
                }
                
            }
        }

        private void DefualtPlayListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                if (isDefaultComplete)
                {
                    ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                    AppSettings.DefualtPlayList = selectedItem.Content.ToString();
                }                
                if (!isInitializing)
                {
                    SaveSetting();
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
                    AppSettings.DefualtEntry = selectedItem.Content.ToString();
                }                
                if (!isInitializing)
                {
                    SaveSetting();
                }
            }
        }
    }
}
