using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>显示带左侧或底部叠字的专辑拼贴；点击不执行操作，详情交互由页面转发。</summary>
public sealed partial class HomeCarouselTile : UserControl
{
    /// <summary>标识拼贴曲目。</summary>
    public static readonly DependencyProperty SongProperty = DependencyProperty.Register(
        nameof(Song), typeof(HomeSong), typeof(HomeCarouselTile), new PropertyMetadata(null, SongChanged));
    /// <summary>标识上方的大拼贴采用左侧居中标题。</summary>
    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.Register(
        nameof(IsPrimary), typeof(bool), typeof(HomeCarouselTile), new PropertyMetadata(false, LayoutChanged));
    /// <summary>获取或设置拼贴曲目。</summary>
    public HomeSong? Song { get => (HomeSong?)GetValue(SongProperty); set => SetValue(SongProperty, value); }
    /// <summary>获取或设置是否采用大拼贴的标题布局。</summary>
    public bool IsPrimary { get => (bool)GetValue(IsPrimaryProperty); set => SetValue(IsPrimaryProperty, value); }

    /// <summary>初始化拼贴及响应式标题布局。</summary>
    public HomeCarouselTile()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }
    private static void SongChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var tile = (HomeCarouselTile)sender;
        tile.Artwork.Music = tile.Song?.Music;
    }
    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HomeCarouselTile)sender).UpdateLayoutMode();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutMode();
    private void UpdateLayoutMode()
    {
        bool compact = ActualWidth < 230;
        TitleText.FontSize = compact ? 14 : IsPrimary ? 24 : 16;
        TitleText.Margin = new Thickness(ActualWidth < 180 ? 8 : IsPrimary ? 32 : 16);
        TitleText.VerticalAlignment = IsPrimary ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        TitleText.HorizontalAlignment = HorizontalAlignment.Left;
        TitleText.MaxWidth = System.Math.Max(0, ActualWidth - TitleText.Margin.Left - TitleText.Margin.Right);
        Shade.EndPoint = IsPrimary ? new Point(1, 0) : new Point(.25, 1);
        ShadeStart.Offset = IsPrimary ? 0 : .2;
        ShadeStart.Color = IsPrimary ? Color.FromArgb(184, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0);
        ShadeEnd.Color = IsPrimary ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(184, 0, 0, 0);
    }
}
