using Microsoft.UI.Xaml.Controls;
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
        public SettingsPage()
        {
            this.InitializeComponent();
            DateTime dateTime = DateTime.Now;
            InitializeDatabase();
            LoadOutputDevices();
            InitializeSettings();
            InitializeOutputDevices();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<SaveSettings>();
        }

        private void InitializeOutputDevices()
        {
            Task.Run(() =>
            {
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    outputDeviceList.Add(device);
                }
            });
        }

        private async Task SaveSetting()
        {
            var settings = await dbConnection.Table<SaveSettings>().FirstOrDefaultAsync();
            SaveSettings newSettings = new SaveSettings();
            newSettings.OutputMode = AppSettings.OutputMode;
            newSettings.Latency = AppSettings.Latency;
            newSettings.DeviceFriendlyName = AppSettings.DeviceName;
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
            List<string> outputDeviceList = AppSettings.outputDeviceList;
            foreach (var device in outputDeviceList)
            {
                OutputDeviceComboBox.Items.Add(new ComboBoxItem { Content = device });
            }
        }

        private void InitializeSettings()
        {
            isInitializing = true;
            DateTime dateTime = DateTime.Now;
            // 设置初始的输出模式
            foreach (ComboBoxItem item in OutputModeComboBox.Items)
            {
                if (item.Content.ToString() == AppSettings.OutputMode)
                {
                    OutputModeComboBox.SelectedItem = item;
                    break;
                }
            }
            System.Diagnostics.Debug.WriteLine($"OutputModeComboBox 初始化完成，耗时：{(DateTime.Now - dateTime).TotalMilliseconds}ms");

            // 设置初始的输出设备
            foreach (ComboBoxItem item in OutputDeviceComboBox.Items)
            {
                //MMDevice itemDevice = (MMDevice)item.Tag;
                if (item.Content.ToString() == AppSettings.DeviceName)
                {
                    OutputDeviceComboBox.SelectedItem = item;
                    break;
                }
            }
            System.Diagnostics.Debug.WriteLine($"OutputDeviceComboBox 初始化完成，耗时：{(DateTime.Now - dateTime).TotalMilliseconds}ms");
            // 设置初始的缓冲区大小
            LatencyTextBox.Text = AppSettings.Latency.ToString();
            isInitializing = false;
        }

        private void OutputModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppSettings.OutputMode = selectedItem.Content.ToString();
                if (!isInitializing)
                {
                    AppSettings.OnOutputSettingsChanged();
                }
                SaveSetting();
            }
        }

        private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppSettings.DeviceName = selectedItem.Content.ToString();
                OutputDeviceChanged();
                SaveSetting();
            }
        }

        private void OutputDeviceChanged()
        {
            Task.Run(() =>
            {
                if (outputDeviceList.Count > 0)
                {
                    foreach (var device in outputDeviceList)
                    {
                        if (device.FriendlyName == AppSettings.DeviceName)
                        {
                            AppSettings.OutputDevice.mMDevice = device;
                            if (!isInitializing)
                            {
                                AppSettings.OnOutputSettingsChanged();
                            }
                            break;
                        }
                    }
                }
            });
        }

        private void LatencyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(LatencyTextBox.Text, out int latency))
            {
                AppSettings.Latency = latency;
                if (!isInitializing)
                {
                    AppSettings.OnOutputSettingsChanged();
                }
                SaveSetting();
            }
        }
    }
}
