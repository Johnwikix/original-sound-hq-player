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
        /// 热度图整体等比适配：scale = 宿主宽 / 内容原始宽（无下限，窄窗口整体缩小，不出现滚动条），
        /// 垂直方向随比例同步放大，卡片高度按 154 × scale 撑开，由外层页面滚动条接管。
        /// </summary>
        private void UpdateHeatmapScale()
        {
            int cols = (ViewModel.HeatmapData.Count + 6) / 7;
            if (cols == 0 || HeatmapHost.ActualWidth <= 0)
            {
                return;
            }

            double contentWidth = HeatmapWeekdayColumn.ActualWidth + cols * 18.0;
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

            AnimateScale(scale);
            HeatmapContent.Height = HeatmapLayoutHeight * scale;
        }

        private void AnimateScale(double toScale)
        {
            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                To = toScale,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            };
            Storyboard.SetTarget(animation, HeatmapScale);
            Storyboard.SetTargetProperty(animation, "ScaleX");
            storyboard.Children.Add(animation);

            var animationY = new DoubleAnimation
            {
                To = toScale,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 }
            };
            Storyboard.SetTarget(animationY, HeatmapScale);
            Storyboard.SetTargetProperty(animationY, "ScaleY");
            storyboard.Children.Add(animationY);

            storyboard.Begin();
        }
    }
}