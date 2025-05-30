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

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    public sealed partial class EqualizerDialog : ContentDialog
    {
        private Dictionary<string, double[]> _presets;
        private List<Slider> _sliders;

        public EqualizerDialog()
        {
            this.InitializeComponent();
            InitializePresets();
            InitializeSliders();

            // 设置默认预设
            //PresetComboBox.SelectedIndex = 0;
        }

        private void InitializeSliders()
        {
            _sliders = new List<Slider>
            {
                Slider32Hz, Slider64Hz, Slider125Hz, Slider250Hz, Slider500Hz,
                Slider1kHz, Slider2kHz, Slider4kHz, Slider8kHz, Slider16kHz
            };
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

        private void OnSliderValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (sender is Slider slider)
            {
                string frequency = slider.Tag?.ToString() ?? "Unknown";
                double value = Math.Round(slider.Value, 1);

                // 这里可以添加实际的均衡器逻辑
                // ApplyEqualizerSettings();
            }
        }

        private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            //if (PresetComboBox.SelectedItem is ComboBoxItem selectedItem &&
            //    selectedItem.Tag?.ToString() is string presetName &&
            //    _presets.ContainsKey(presetName))
            //{
            //    var values = _presets[presetName];

            //    for (int i = 0; i < _sliders.Count && i < values.Length; i++)
            //    {
            //        _sliders[i].Value = values[i];
            //    }
            //}
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

        // 重置所有滑块到0
        private void ResetSliders()
        {
            foreach (var slider in _sliders)
            {
                slider.Value = 0;
            }

            //PresetComboBox.SelectedIndex = 0; // 选择"平坦"预设
        }

        private void ToggleSwitchEqualizer_Toggled(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"Equalizer toggled: {ToggleSwitchEqualizer.IsOn}");
            AppData.IsEqualizerEnabled = ToggleSwitchEqualizer.IsOn;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }
    }
}
