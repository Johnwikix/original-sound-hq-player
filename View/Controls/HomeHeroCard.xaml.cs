using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>复现 Store 主轮播的满幅图片、左侧渐隐叠字和随封面取色的动作区域。</summary>
public sealed partial class HomeHeroCard : UserControl
{
    private readonly ScaleTransform _imageScale = new();
    private readonly CompositeTransform _titleTransform = new();
    private readonly CompositeTransform _descriptionTransform = new();
    private readonly CompositeTransform _buttonTransform = new();
    private readonly Storyboard _imageTransition = new();
    private readonly Storyboard _textTransition = new();
    private readonly UISettings _settings = new();
    /// <summary>标识主题快照。</summary>
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(HomeTheme), typeof(HomeHeroCard), new PropertyMetadata(null, ThemeChanged));
    /// <summary>获取或设置当前主题。</summary>
    public HomeTheme? Theme { get => (HomeTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    /// <summary>初始化主轮播展示，不自行读取图片文件。</summary>
    public HomeHeroCard()
    {
        InitializeComponent();
        Translation = new Vector3(0, 0, 16);
        Artwork.RenderTransform = _imageScale;
        AddTransition(_imageTransition, _imageScale, "ScaleX", 1.04, 1, 0);
        AddTransition(_imageTransition, _imageScale, "ScaleY", 1.04, 1, 0);
        AddTransition(_imageTransition, Artwork, "Opacity", .45, 1, 0);
        AddTextTransition(TitleText, _titleTransform, 0, 1);
        AddTextTransition(DescriptionText, _descriptionTransform, 60, .8);
        AddTextTransition(PlayButton, _buttonTransform, 120, 1);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Artwork.ArtworkColorChanged += ArtworkChanged;
        UpdateTone(Artwork.ArtworkColor);
        PlayTextTransition();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Artwork.ArtworkColorChanged -= ArtworkChanged;
        _imageTransition.Stop();
        _textTransition.Stop();
        _imageScale.ScaleX = 1;
        _imageScale.ScaleY = 1;
        Artwork.Opacity = 1;
        ResetText(TitleText, _titleTransform, 1);
        ResetText(DescriptionText, _descriptionTransform, .8);
        ResetText(PlayButton, _buttonTransform, 1);
    }
    private void AddTextTransition(FrameworkElement element, CompositeTransform transform, int delay, double opacity)
    {
        element.RenderTransform = transform;
        AddTransition(_textTransition, transform, "TranslateX", 52, 0, delay);
        AddTransition(_textTransition, transform, "ScaleX", .94, 1, delay);
        AddTransition(_textTransition, transform, "ScaleY", .94, 1, delay);
        AddTransition(_textTransition, element, "Opacity", 0, opacity, delay);
    }
    private static void AddTransition(Storyboard storyboard, DependencyObject target, string property, double from, double to, int delay)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(420),
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
    private static void ThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (HomeHeroCard)sender;
        if (card.IsLoaded) card.PlayTextTransition();
    }
    private static void ResetText(FrameworkElement element, CompositeTransform transform, double opacity)
    {
        element.Opacity = opacity;
        transform.TranslateX = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
    }
    private static void PrepareText(FrameworkElement element, CompositeTransform transform)
    {
        element.Opacity = 0;
        transform.TranslateX = 52;
        transform.ScaleX = .94;
        transform.ScaleY = .94;
    }
    private void PlayTextTransition()
    {
        _textTransition.Stop();
        // 动画和变换按卡片复用；关闭系统动画时直接呈现最终状态。
        ResetText(TitleText, _titleTransform, 1);
        ResetText(DescriptionText, _descriptionTransform, .8);
        ResetText(PlayButton, _buttonTransform, 1);
        if (!_settings.AnimationsEnabled) return;
        PrepareText(TitleText, _titleTransform);
        PrepareText(DescriptionText, _descriptionTransform);
        PrepareText(PlayButton, _buttonTransform);
        _textTransition.Begin();
    }
    private void ArtworkChanged(Color color)
    {
        UpdateTone(color);
        _imageTransition.Stop();
        if (_settings.AnimationsEnabled) _imageTransition.Begin();
    }
    private void UpdateTone(Color color)
    {
        if (color.A == 0) color = Color.FromArgb(255, 32, 32, 32);
        byte red = (byte)(color.R * .32);
        byte green = (byte)(color.G * .32);
        byte blue = (byte)(color.B * .32);
        ShadeStart.Color = Color.FromArgb(245, red, green, blue);
        ShadeMiddle.Color = Color.FromArgb(216, red, green, blue);
        ActionBrush.Color = Color.FromArgb(255, (byte)(color.R * .5), (byte)(color.G * .5), (byte)(color.B * .5));
        ActionBorderBrush.Color = color;
        ActionHoverBrush.Color = Color.FromArgb(255, (byte)(color.R * .65), (byte)(color.G * .65), (byte)(color.B * .65));
        ActionPressedBrush.Color = Color.FromArgb(255, red, green, blue);
    }
}
