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
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using WinUIMusicPlayer.Model;
using NAudio.CoreAudioApi;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            this.InitializeComponent();
            LoadOutputDevices();
            InitializeSettings();
        }

        private async void LoadOutputDevices()
        {
            MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                OutputDeviceComboBox.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device });
            }
            OutputDeviceComboBox.SelectedIndex = 0;
        }

        private void InitializeSettings()
        {
            // 设置初始的输出模式
            foreach (ComboBoxItem item in OutputModeComboBox.Items)
            {
                if (item.Content.ToString() == AppSettings.OutputMode)
                {
                    OutputModeComboBox.SelectedItem = item;
                    break;
                }
            }

            // 设置初始的输出设备
            foreach (ComboBoxItem item in OutputDeviceComboBox.Items)
            {
                if (item.Tag == AppSettings.OutputDevice.mMDevice)
                {
                    OutputDeviceComboBox.SelectedItem = item;
                    break;
                }
            }

            // 设置初始的缓冲区大小
            LatencyTextBox.Text = AppSettings.Latency.ToString();
        }

        private void OutputModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppSettings.OutputMode = selectedItem.Content.ToString();
                AppSettings.OnOutputSettingsChanged();
            }
        }

        private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                ComboBoxItem selectedItem = (ComboBoxItem)e.AddedItems[0];
                AppSettings.OutputDevice.mMDevice = (MMDevice)selectedItem.Tag;
                AppSettings.OnOutputSettingsChanged();
            }
        }

        private void LatencyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(LatencyTextBox.Text, out int latency))
            {
                AppSettings.Latency = latency;
                AppSettings.OnOutputSettingsChanged();
            }
        }
    }
}
