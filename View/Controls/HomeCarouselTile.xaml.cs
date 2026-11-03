using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>显示带左侧或底部叠字的专辑拼贴；点击与键盘请求由宿主转发到歌曲详情。</summary>
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
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);

    }
    private static void SongChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var tile = (HomeCarouselTile)sender;
        tile.Artwork.Music = tile.Song?.Music;
    }
    private double _measureWidth = double.NaN;
    private double _measureHeight = double.NaN;
    private bool _measurePrimary;
    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HomeCarouselTile)sender).InvalidateMeasure();
    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Measure(this, availableSize);
        if (double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height) &&
            (_measureWidth != availableSize.Width || _measureHeight != availableSize.Height || _measurePrimary != IsPrimary))
        {
            double width = availableSize.Width;
            double height = availableSize.Height;
            bool compact = width < 230 || height < 90;
            TitleText.FontSize = compact ? 14 : IsPrimary ? 24 : 16;
            TitleText.Margin = new Thickness(width < 180 || height < 90 ? 8 : IsPrimary ? 32 : 16);
            TitleText.VerticalAlignment = IsPrimary ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            TitleText.HorizontalAlignment = HorizontalAlignment.Left;
            TitleText.MaxLines = height < 64 ? 1 : 2;
            TitleText.Visibility = height < 32 ? Visibility.Collapsed : Visibility.Visible;
            TitleText.MaxWidth = System.Math.Max(0, width - TitleText.Margin.Left - TitleText.Margin.Right);
            Shade.EndPoint = IsPrimary ? new Point(1, 0) : new Point(.25, 1);
            ShadeStart.Offset = IsPrimary ? 0 : .2;
            ShadeStart.Color = IsPrimary ? Color.FromArgb(184, 0, 0, 0) : Color.FromArgb(0, 0, 0, 0);
            ShadeEnd.Color = IsPrimary ? Color.FromArgb(0, 0, 0, 0) : Color.FromArgb(184, 0, 0, 0);
            _measureWidth = width;
            _measureHeight = height;
            _measurePrimary = IsPrimary;
        }
        return base.MeasureOverride(availableSize);
    }
}
