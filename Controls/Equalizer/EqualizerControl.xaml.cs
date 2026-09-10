using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls.Equalizer
{
    /// <summary>
    /// 10 段均衡器编辑控件：左侧 dB 刻度（+12 ~ -12，按滑块行程对齐），
    /// 右侧频段滑条 + 实时增益读数 + 频率标签，0 dB 中线贯穿滑条区。
    /// 编辑去抖由宿主（EqualizerDialog）负责。
    /// </summary>
    public sealed partial class EqualizerControl : UserControl
    {
        private const int BandCount = 10;
        /// <summary>WinUI 默认滑块直径：dB 刻度按“滑块中心行程”对齐（值域端点位于滑块中心）。</summary>
        private const double ThumbSize = 20;
        private const double ScaleLabelHeight = 14;
        private const double WheelStepDb = 1.0;

        private readonly Slider[] _sliders = new Slider[BandCount];
        private readonly TextBlock[] _gainLabels = new TextBlock[BandCount];
        private EqBand[] _bands = Array.Empty<EqBand>();
        private bool _isSyncing;

        /// <summary>用户编辑某频段增益后触发（编程赋值不触发），参数为频段索引；增益已写回 <see cref="EqBand"/>。</summary>
        public event EventHandler<int>? GainEdited;

        public EqualizerControl()
        {
            InitializeComponent();
            BuildBands();
            DbScaleCanvas.SizeChanged += (_, _) => RebuildDbScale();
            Loaded += (_, _) => RebuildDbScale();
        }

        /// <summary>绑定运行时频段状态并刷新滑条（保存数组引用，用户编辑直接写回该数组）。</summary>
        public void SetBands(EqBand[] bands)
        {
            _bands = bands ?? Array.Empty<EqBand>();
            _isSyncing = true;
            for (int i = 0; i < BandCount; i++)
            {
                double gain = i < _bands.Length ? _bands[i].GainDb : 0;
                _sliders[i].Value = gain;
                _gainLabels[i].Text = EqualizerHelper.FormatGain(gain);
            }
            _isSyncing = false;
        }

        private void BuildBands()
        {
            for (int i = 0; i < BandCount; i++)
            {
                double frequency = EqualizerHelper.StandardFrequencies[i];

                var gainLabel = new TextBlock { Style = (Style)Resources["EqGainLabelStyle"] };
                var slider = new Slider { Style = (Style)Resources["EqSliderStyle"], Tag = i };
                slider.ValueChanged += OnSliderValueChanged;
                slider.PointerWheelChanged += OnSliderPointerWheel;
                _sliders[i] = slider;
                _gainLabels[i] = gainLabel;

                var band = new StackPanel { Spacing = 0 };
                band.Children.Add(new Border { Child = gainLabel, Height = 18, Margin = new Thickness(0, 0, 0, 4) });
                band.Children.Add(slider);
                band.Children.Add(new TextBlock
                {
                    Style = (Style)Resources["EqFreqLabelStyle"],
                    Text = EqualizerHelper.GetBandLabel(frequency),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                BandsPanel.Children.Add(band);
            }
        }

        private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider slider) return;
            int index = (int)slider.Tag;
            double gain = Math.Round(slider.Value, 1);
            if (index < _bands.Length) _bands[index].GainDb = gain;
            _gainLabels[index].Text = EqualizerHelper.FormatGain(gain);
            if (!_isSyncing) GainEdited?.Invoke(this, index);
        }

        private void OnSliderPointerWheel(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Slider slider || !slider.IsEnabled) return;
            int delta = e.GetCurrentPoint(slider).Properties.MouseWheelDelta;
            if (delta == 0) return;
            double step = delta > 0 ? WheelStepDb : -WheelStepDb;
            slider.Value = Math.Clamp(slider.Value + step, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }

        /// <summary>重建 dB 刻度：+12 ~ -12 每 3 dB 一档，纵坐标按滑块中心行程线性映射。</summary>
        private void RebuildDbScale()
        {
            DbScaleCanvas.Children.Clear();
            double height = DbScaleCanvas.ActualHeight;
            if (height <= ThumbSize) return;

            for (int db = 12; db >= -12; db -= 3)
            {
                var label = new TextBlock
                {
                    Style = (Style)Resources[db == 0 ? "EqScaleLabelZeroStyle" : "EqScaleLabelStyle"],
                    Text = db > 0 ? $"+{db}" : db.ToString()
                };
                double travel = height - ThumbSize;
                double y = ThumbSize / 2 + (12 - db) / 24.0 * travel;
                Canvas.SetTop(label, y - ScaleLabelHeight / 2);
                Canvas.SetLeft(label, 0);
                DbScaleCanvas.Children.Add(label);
            }
        }
    }
}
