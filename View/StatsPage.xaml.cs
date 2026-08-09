using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;
using System.Globalization;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// 播放统计页面：展示选定时间范围内的收听统计（总时长、热度图、时段活跃度、Top 歌曲 / 歌手 / 专辑）。
    /// </summary>
    public sealed partial class StatsPage : Page
    {
        /// <summary>热度图内容原始布局高度（月份行 20 + 7×18 格子 + 底部余量 8），缩放时按此比例放大卡片高度。</summary>
        private const double HeatmapLayoutHeight = 20 + 7 * 18 + 8;

        public StatsViewModel ViewModel { get; }

        public StatsPage()
        {
            ViewModel = App.Services.GetRequiredService<StatsViewModel>();
            this.InitializeComponent();
            DataContext = this;
            this.NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.PropertyChanged += OnStatsViewModelPropertyChanged;
            ViewModel.OnPageActive();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.PropertyChanged -= OnStatsViewModelPropertyChanged;
            ViewModel.OnPageInactive();
        }

        /// <summary>按当前 UI 文化设置热度图左侧 7 个星期刻度（首日起，逐行递增）。</summary>
        private void StatsPage_Loaded(object sender, RoutedEventArgs e)
        {
            var dtfi = CultureInfo.CurrentUICulture.DateTimeFormat;
            int first = (int)dtfi.FirstDayOfWeek;
            HeatmapLabel0.Text = dtfi.GetDayName((DayOfWeek)(first % 7));
            HeatmapLabel1.Text = dtfi.GetDayName((DayOfWeek)((first + 1) % 7));
            HeatmapLabel2.Text = dtfi.GetDayName((DayOfWeek)((first + 2) % 7));
            HeatmapLabel3.Text = dtfi.GetDayName((DayOfWeek)((first + 3) % 7));
            HeatmapLabel4.Text = dtfi.GetDayName((DayOfWeek)((first + 4) % 7));
            HeatmapLabel5.Text = dtfi.GetDayName((DayOfWeek)((first + 5) % 7));
            HeatmapLabel6.Text = dtfi.GetDayName((DayOfWeek)((first + 6) % 7));
        }

        private void OnStatsViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.HeatmapData))
            {
                // 数据替换后内容宽度变化，等布局完成后按新列数重算缩放。
                _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, UpdateHeatmapScale);
            }
        }

        private void HeatmapHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHeatmapScale();
        }

        /// <summary>
        /// 热度图整体等比适配：scale = 宿主可用宽 / 内容实际宽（无下限，窄窗口整体缩小，不出现滚动条）；
        /// 内容宽以 HeatmapScaled 实测为准（月份标签/换行网格的实际布局宽，可能与列数公式有数像素偏差），
        /// 左右各留 32px 边距（HeatmapContent.Margin），保证任意宽度下内容左右间距对称且不裁剪。
        /// </summary>
        private void UpdateHeatmapScale()
        {
            int cols = (ViewModel.HeatmapData.Count + 6) / 7;
            if (cols == 0 || HeatmapHost.ActualWidth <= 0)
            {
                return;
            }

            double contentWidth = HeatmapScaled.ActualWidth > 0
                ? HeatmapScaled.ActualWidth
                : HeatmapWeekdayColumn.ActualWidth + cols * 18.0;
            if (contentWidth <= 0)
            {
                return;
            }

            double availableWidth = HeatmapHost.ActualWidth
                                    - HeatmapContent.Margin.Left
                                    - HeatmapContent.Margin.Right;
            double scale = availableWidth / contentWidth;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                return;
            }

            ApplyScale(scale);
            SetHeatmapHeight(HeatmapLayoutHeight * scale);
        }

        #region 缩放应用（直接赋值，保持原生渲染锐利）

        private const double ScaleEpsilon = 0.001;

        private double _lastAppliedScale;

        /// <summary>
        /// 应用缩放：直接赋值 ScaleX/ScaleY，不走合成器动画。
        /// 动画会让子树以纹理层方式光栅化并按旧 DPI 采样（跨屏拖动恢复锐利即此症状），
        /// 直接赋值走原生重绘路径，任意 DPI/比例下文字都保持锐利。
        /// </summary>
        private void ApplyScale(double toScale)
        {
            if (Math.Abs(toScale - _lastAppliedScale) < ScaleEpsilon)
            {
                return;
            }

            HeatmapScale.ScaleX = toScale;
            HeatmapScale.ScaleY = toScale;
            _lastAppliedScale = toScale;
        }

        #endregion

        #region 布局盒同步（30Hz 节流 + 末值补齐）

        private long _lastHeightSetMs;
        private double _pendingHeight;
        private bool _heightSettleQueued;

        /// <summary>
        /// Height 为布局属性，拖动窗口时按 ≥33ms（约 30Hz）节流，撑开卡片高度以匹配缩放后的视觉高度；
        /// 节流中被跳过的末值经 DispatcherQueue 惰性补齐，保证最终高度准确。
        /// </summary>
        private void SetHeatmapHeight(double height)
        {
            long now = Environment.TickCount64;
            if (now - _lastHeightSetMs >= 33)
            {
                HeatmapContent.Height = height;
                _lastHeightSetMs = now;
                return;
            }

            _pendingHeight = height;
            if (_heightSettleQueued)
            {
                return;
            }

            _heightSettleQueued = true;
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SettleHeatmapHeight);
        }

        private void SettleHeatmapHeight()
        {
            _heightSettleQueued = false;
            HeatmapContent.Height = _pendingHeight;
            _lastHeightSetMs = Environment.TickCount64;
        }

        #endregion
    }
}