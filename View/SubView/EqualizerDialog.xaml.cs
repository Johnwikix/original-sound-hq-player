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
using WinUIMusicPlayer.Model;
using System.Diagnostics;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    public sealed partial class EqualizerDialog : ContentDialog
    {
        private Dictionary<string, double[]> _presets;
        private List<Slider> _sliders;
        public EventHandler<string> EqualizerGainChanged;
        public EventHandler clearEqualizer;
        private bool _isInitialized = false;

        public EqualizerDialog()
        {
            this.InitializeComponent();
            _isInitialized = true;
            InitializePresets();
            InitializingSettings();
            _isInitialized = false;
            // 设置默认预设
            //PresetComboBox.SelectedIndex = 0;
        }

        private void InitializingSettings()
        {
            _sliders = new List<Slider>
            {
                Slider32Hz, Slider64Hz, Slider125Hz, Slider250Hz, Slider500Hz,
                Slider1kHz, Slider2kHz, Slider4kHz, Slider8kHz, Slider16kHz
            };
            foreach (ComboBoxItem item in ComboBoxPresets.Items)
            {
                if (item.Tag?.ToString() == AppSettings.EqualizerPreset)
                {
                    ComboBoxPresets.SelectedItem = item;
                    break;
                }
            }
            InitializeSliders();            
            ToggleSwitchEqualizer.IsOn = AppSettings.IsEqualizerEnabled;
        }

        private void InitializeSliders()
        {
            // 初始化滑块列表
            Slider32Hz.Value = AppSettings.equalizer["32Hz"];
            Slider64Hz.Value = AppSettings.equalizer["64Hz"];
            Slider125Hz.Value = AppSettings.equalizer["125Hz"];
            Slider250Hz.Value = AppSettings.equalizer["250Hz"];
            Slider500Hz.Value = AppSettings.equalizer["500Hz"];
            Slider1kHz.Value = AppSettings.equalizer["1kHz"];
            Slider2kHz.Value = AppSettings.equalizer["2kHz"];
            Slider4kHz.Value = AppSettings.equalizer["4kHz"];
            Slider8kHz.Value = AppSettings.equalizer["8kHz"];
            Slider16kHz.Value = AppSettings.equalizer["16kHz"];
        }

        private void InitializePresets()
        {
            _presets = new Dictionary<string, double[]>
            {
                ["Flat"] = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                ["Pop"] = new double[] { -1, -0.5, 0, 2, 4, 4, 2, 0, -1, -2 },
                ["Rock"] = new double[] { 4, 3, 2, 1, -0.5, -1, 0, 2, 4, 5 },
                ["Jazz"] = new double[] { 2, 1, 0, 1, 2, 2, 1, 1, 2, 3 },
                ["Classical"] = new double[] { 3, 2, 1, 0, 0, 0, -1, -1, 1, 2 },
                ["Electronic"] = new double[] { 3, 2, 0, -1, -0.5, 1, 2, 3, 4, 5 },
                ["Vocal"] = new double[] { -2, -1, 0, 1, 3, 4, 4, 3, 1, 0 }
            };
        }

        private async void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (sender is Slider slider)
            {
                if (ComboBoxPresets.SelectedItem is ComboBoxItem selectedItem)
                {
                    string frequency = slider.Tag?.ToString() ?? "Unknown";
                    double value = Math.Round(slider.Value, 1);
                    AppSettings.equalizer[frequency] = value;                  
                    string? presetName = selectedItem.Tag.ToString();
                    if (presetName == "Custom")
                    {
                        await MusicDatabaseService.UpdateEqualizerSettings(ToolUtils.ConvertToJson(AppSettings.equalizer), AppSettings.IsEqualizerEnabled);
                    }
                    EqualizerGainChanged?.Invoke(this, frequency);
                }                
            }
        }

        // 获取当前均衡器设置
        public Dictionary<string, double> GetEqualizerSettings()
        {
            var settings = new Dictionary<string, double>();
            foreach (var slider in _sliders)
            {
                if (slider.Tag?.ToString() is string frequency)
                {
                    settings[frequency] = Math.Round(slider.Value, 1);
                }
            }
            return settings;
        }

        // 设置均衡器值
        public void SetEqualizerSettings(Dictionary<string, double> settings)
        {
            foreach (var slider in _sliders)
            {
                if (slider.Tag?.ToString() is string frequency &&
                    settings.ContainsKey(frequency))
                    {
                        slider.Value = settings[frequency];
                    }
            }
        }

        private void ToggleSwitchEqualizer_Toggled(object sender, RoutedEventArgs e)
        {
            AppSettings.IsEqualizerEnabled = ToggleSwitchEqualizer.IsOn;
            DispatcherQueue.TryEnqueue(() =>
            {
                clearEqualizer?.Invoke(this, EventArgs.Empty);
            });
            
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings settings = await MusicDatabaseService.GetSettings();
            if (settings != null)
            {
                settings.IsEqualizerEnabled = AppSettings.IsEqualizerEnabled;
                settings.EqualizerPreset = AppSettings.EqualizerPreset;
                await MusicDatabaseService.UpdateSettings(settings);
            }           
            this.Hide();
        }

        private async void ComboBoxPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboBoxPresets.SelectedItem is ComboBoxItem selectedItem)
            {
                string? presetName = selectedItem.Tag.ToString();
                AppSettings.EqualizerPreset = presetName ?? "Custom";
                if (_presets.ContainsKey(presetName))
                {
                    var presetValues = _presets[presetName];
                    for (int i = 0; i < _sliders.Count; i++)
                    {
                        _sliders[i].Value = presetValues[i];
                        string frequency = _sliders[i].Tag?.ToString() ?? "Unknown";
                        AppSettings.equalizer[frequency] = presetValues[i];
                    }
                }
                else if (presetName == "Custom") {
                    SaveSettings settings = await MusicDatabaseService.GetSettings();
                    AppSettings.equalizer = ToolUtils.ConvertToDictionary(settings.equalizerStr);
                    InitializeSliders();
                }
            }
        }
    }
}
