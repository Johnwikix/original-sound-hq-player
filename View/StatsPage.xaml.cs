using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Globalization;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// 播放统计页面：展示选定时间范围内的收听统计（总时长、热度图、时段活跃度、Top 歌曲 / 歌手 / 专辑）。
    /// </summary>
    public sealed partial class StatsPage : Page
    {
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
            ViewModel.OnPageActive();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.OnPageInactive();
        }

        /// <summary>按当前 UI 文化设置热度图左侧星期标签（首日 +2 / +4 天）。</summary>
        private void StatsPage_Loaded(object sender, RoutedEventArgs e)
        {
            var dtfi = CultureInfo.CurrentUICulture.DateTimeFormat;
            HeatmapLabel1.Text = dtfi.GetDayName((DayOfWeek)(((int)dtfi.FirstDayOfWeek + 1) % 7));
            HeatmapLabel3.Text = dtfi.GetDayName((DayOfWeek)(((int)dtfi.FirstDayOfWeek + 3) % 7));
            HeatmapLabel5.Text = dtfi.GetDayName((DayOfWeek)(((int)dtfi.FirstDayOfWeek + 5) % 7));
        }
    }
}