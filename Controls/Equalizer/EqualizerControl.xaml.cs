using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls.Equalizer
{
    /// <summary>
    /// 10 段均衡器编辑控件：左侧 dB 刻度（+12 ~ -12，按滑块行程对齐），
    /// 右侧频段滑条、实时增益读数、频率标签和独立 Q 数值输入。
    /// 编辑去抖由宿主（EqualizerDialog）负责。
    /// </summary>
    public sealed partial class EqualizerControl : UserControl
    {
        private const int BandCount = 10;
        private const double WheelStepDb = 1.0;

        private readonly Slider[] _sliders = new Slider[BandCount];
        private readonly TextBlock[] _gainLabels = new TextBlock[BandCount];
        private readonly NumberBox[] _qBoxes = new NumberBox[BandCount];
        private EqBand[] _bands = Array.Empty<EqBand>();
        private bool _isSyncing;

        /// <summary>用户编辑某频段增益或 Q 后触发（编程赋值不触发），参数为频段索引；参数已写回 <see cref="EqBand"/>。</summary>
        public event EventHandler<int>? BandEdited;

        public EqualizerControl()
        {
            InitializeComponent();
            BuildBands();
            DbScaleCanvas.SizeChanged += (_, _) => RebuildDbScale();
            Loaded += (_, _) => RebuildDbScale();
            _sliders[0].Loaded += OnScaleSliderLoaded;
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
                _qBoxes[i].Value = i < _bands.Length ? EqualizerHelper.ClampQ(_bands[i].Q) : EqPreset.DefaultQ;
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

                var qBox = new NumberBox
                {
                    Tag = i, Width = 72, MinWidth = 0, Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Minimum = EqPreset.MinQ, Maximum = EqPreset.MaxQ,
                    Value = EqPreset.DefaultQ, SmallChange = 0.1, LargeChange = 1,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                    NumberFormatter = new DecimalFormatter { IntegerDigits = 1, FractionDigits = 0,
                        NumberRounder = new IncrementNumberRounder { Increment = 0.001 } },
                    Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6, 0, 6, 0), FontSize = 12,
                };
                AutomationProperties.SetName(qBox, $"{frequency:0} Hz Q");
                AutomationProperties.SetAutomationId(qBox, $"EqQ{i}");
                ToolTipService.SetToolTip(qBox, ToolUtils.GetString("EqQDescription"));
                qBox.ValueChanged += OnQValueChanged;
                qBox.Loaded += OnQBoxLoaded;
                _qBoxes[i] = qBox;
                AutomationProperties.SetName(slider, $"{frequency:0} Hz dB");
                var band = new StackPanel { Spacing = 0, Width = 78 };
                band.Children.Add(new Border { Child = gainLabel, Height = 18, Margin = new Thickness(0, 0, 0, 4) });
                band.Children.Add(slider);
                band.Children.Add(new TextBlock
                {
                    Style = (Style)Resources["EqFreqLabelStyle"],
                    Text = EqualizerHelper.GetBandLabel(frequency),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                band.Children.Add(new TextBlock
                {
                    Style = (Style)Resources["EqGainLabelStyle"],
                    Text = "Q", Margin = new Thickness(0, 6, 0, 0),
                });
                band.Children.Add(qBox);
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
            if (!_isSyncing) BandEdited?.Invoke(this, index);
        }

        private void OnQValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isSyncing || sender.Tag is not int index || index >= _bands.Length) return;
            double q = double.IsFinite(args.NewValue) ? EqualizerHelper.ClampQ(args.NewValue) : _bands[index].Q;
            q = Math.Round(q, 3);
            _isSyncing = true;
            sender.Value = q;
            _isSyncing = false;
            if (_bands[index].Q == q) return;
            _bands[index].Q = q;
            BandEdited?.Invoke(this, index);
        }

        private static void OnQBoxLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not NumberBox numberBox) return;
            numberBox.ApplyTemplate();
            // 使用原生 NumberBox，避免派生类型在 WinRT 样式匹配时退化为 Control。
            // 默认模板不转发 VerticalContentAlignment，待模板创建后调整内部文本框。
            if (FindTemplatePart<TextBox>(numberBox, "InputBox") is TextBox input)
            {
                input.ApplyTemplate();
                input.VerticalContentAlignment = VerticalAlignment.Center;
                input.TextAlignment = TextAlignment.Center;
                // NumberBoxTextBoxStyle 的 ContentElement 不绑定 VerticalContentAlignment。
                if (FindTemplatePart<ScrollViewer>(input, "ContentElement") is ScrollViewer content)
                {
                    content.VerticalAlignment = VerticalAlignment.Center;
                    content.VerticalContentAlignment = VerticalAlignment.Center;
                }
                if (FindTemplatePart<Button>(input, "DeleteButton") is Button clearButton)
                {
                    clearButton.Visibility = Visibility.Collapsed;
                    clearButton.Opacity = 0;
                    // 焦点状态会用动画将 Visibility 改回 Visible；同时约束宽度，避免占列。
                    clearButton.MinWidth = 0;
                    clearButton.MaxWidth = 0;
                    clearButton.Width = 0;
                    clearButton.Padding = new Thickness(0);
                    clearButton.IsHitTestVisible = false;
                    clearButton.IsTabStop = false;
                }
            }
        }

        private static T? FindTemplatePart<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name) return element;
                if (FindTemplatePart<T>(child, name) is T nested) return nested;
            }
            return null;
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

        private void OnScaleSliderLoaded(object sender, RoutedEventArgs e)
        {
            var slider = _sliders[0];
            slider.ApplyTemplate();
            if (FindTemplatePart<FrameworkElement>(slider, "VerticalTemplate") is FrameworkElement track)
            {
                track.SizeChanged -= OnScaleGeometryChanged;
                track.SizeChanged += OnScaleGeometryChanged;
            }
            if (FindTemplatePart<Thumb>(slider, "VerticalThumb") is Thumb thumb)
            {
                thumb.SizeChanged -= OnScaleGeometryChanged;
                thumb.SizeChanged += OnScaleGeometryChanged;
            }
            RebuildDbScale();
        }

        private void OnScaleGeometryChanged(object sender, SizeChangedEventArgs e) => RebuildDbScale();

        /// <summary>按真实模板的轨道和滑块中心行程定位刻度，不假设滑块高度或控件偏移。</summary>
        private void RebuildDbScale()
        {
            var slider = _sliders[0];
            if (slider == null || !slider.IsLoaded || !DbScaleCanvas.IsLoaded) return;
            var track = FindTemplatePart<FrameworkElement>(slider, "VerticalTemplate");
            var thumb = FindTemplatePart<Thumb>(slider, "VerticalThumb");
            if (track == null || thumb == null || thumb.ActualHeight <= 0 || track.ActualHeight <= thumb.ActualHeight) return;
            double top = track.TransformToVisual(DbScaleCanvas).TransformPoint(new Windows.Foundation.Point()).Y;
            double travel = track.ActualHeight - thumb.ActualHeight;
            DbScaleCanvas.Children.Clear();

            for (int db = 12; db >= -12; db -= 3)
            {
                var label = new TextBlock
                {
                    Style = (Style)Resources[db == 0 ? "EqScaleLabelZeroStyle" : "EqScaleLabelStyle"],
                    Text = db > 0 ? $"+{db}" : db.ToString()
                };
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double y = top + thumb.ActualHeight / 2 + (slider.Maximum - db) / (slider.Maximum - slider.Minimum) * travel;
                Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
                Canvas.SetLeft(label, 0);
                DbScaleCanvas.Children.Add(label);
            }
        }
    }
}
