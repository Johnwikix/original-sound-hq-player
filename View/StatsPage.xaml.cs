using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// 播放统计页面：展示选定时间范围内的收听统计（总时长、Top 歌曲 / 歌手 / 专辑、时段分布）。
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
    }
}