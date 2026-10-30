using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using System;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>绘制左下角的轮播倒计时圆盘；暂停时停掉动画，不另开刷新定时器。</summary>
public sealed partial class CarouselCountdown : UserControl
{
    /// <summary>标识剩余比例，范围为零到一。</summary>
    public static readonly DependencyProperty RemainingProperty = DependencyProperty.Register(
        nameof(Remaining), typeof(double), typeof(CarouselCountdown), new PropertyMetadata(1d, RemainingChanged));
    private readonly Ellipse _full;
    private readonly Path _sector;
    private readonly ArcSegment _arc;
    private readonly Storyboard _storyboard = new();
    private readonly DoubleAnimation _animation = new() { To = 0, EnableDependentAnimation = true };
    private readonly Windows.UI.ViewManagement.UISettings _settings = new();
    /// <summary>获取或设置当前圆盘的剩余比例。</summary>
    public double Remaining { get => (double)GetValue(RemainingProperty); set => SetValue(RemainingProperty, value); }

    /// <summary>初始化一次性复用的几何和动画，圆盘尺寸为 20 像素。</summary>
    public CarouselCountdown()
    {
        Width = 20;
        Height = 20;
        IsHitTestVisible = false;
        var brush = new SolidColorBrush(Colors.DarkGray);
        var background = new Ellipse { Width = 18, Height = 18, Fill = brush, Opacity = .18 };
        _full = new Ellipse { Width = 18, Height = 18, Fill = brush };
        _arc = new ArcSegment { Size = new Size(9, 9), SweepDirection = SweepDirection.Clockwise };
        var figure = new PathFigure { StartPoint = new Point(10, 1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(_arc);
        figure.Segments.Add(new LineSegment { Point = new Point(10, 10) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _sector = new Path { Data = geometry, Fill = brush };
        var layout = new Grid { Width = 20, Height = 20 };
        layout.Children.Add(background);
        layout.Children.Add(_full);
        layout.Children.Add(_sector);
        Content = layout;
        Storyboard.SetTarget(_animation, this);
        Storyboard.SetTargetProperty(_animation, nameof(Remaining));
        _storyboard.Children.Add(_animation);
        Unloaded += OnUnloaded;
        UpdateGeometry();
    }

    /// <summary>从给定剩余比例开始倒计时，复用同一个动画和几何对象。</summary>
    public void Run(double remaining, TimeSpan duration)
    {
        StopAt(remaining);
        if (duration <= TimeSpan.Zero || !_settings.AnimationsEnabled) return;
        _animation.From = Remaining;
        _animation.Duration = duration;
        _storyboard.Begin();
    }
    /// <summary>停止独立动画并准确显示当前剩余量。</summary>
    public void StopAt(double remaining)
    {
        _storyboard.Stop();
        Remaining = Math.Clamp(remaining, 0, 1);
    }
    private static void RemainingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((CarouselCountdown)sender).UpdateGeometry();
    private void UpdateGeometry()
    {
        double fraction = Math.Clamp(Remaining, 0, 1);
        _full.Visibility = fraction >= .9999 ? Visibility.Visible : Visibility.Collapsed;
        _sector.Visibility = fraction is > 0 and < .9999 ? Visibility.Visible : Visibility.Collapsed;
        double angle = fraction * Math.Tau - Math.PI / 2;
        _arc.Point = new Point(10 + 9 * Math.Cos(angle), 10 + 9 * Math.Sin(angle));
        _arc.IsLargeArc = fraction > .5;
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) => _storyboard.Stop();
}
