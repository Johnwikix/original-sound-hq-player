using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>沿用 DevWinUI StoreCarousel 的整组文字动效，图片过渡由封面控件负责。</summary>
public sealed partial class HomeHeroCard : UserControl
{
    private readonly CompositeTransform _textTransform = new();
    private readonly Storyboard _textTransition = new();
    private readonly UISettings _settings = new();
    private readonly CarouselArtworkShadow _shadow;
    /// <summary>标识主题快照。</summary>
    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme), typeof(HomeTheme), typeof(HomeHeroCard), new PropertyMetadata(null, ThemeChanged));
    /// <summary>标识图片是否从右向左滑入。</summary>
    public static readonly DependencyProperty SlideFromRightProperty = DependencyProperty.Register(
        nameof(SlideFromRight), typeof(bool), typeof(HomeHeroCard), new PropertyMetadata(true));
    /// <summary>获取或设置当前主题。</summary>
    public HomeTheme? Theme { get => (HomeTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
    /// <summary>获取或设置图片切换方向。</summary>
    public bool SlideFromRight { get => (bool)GetValue(SlideFromRightProperty); set => SetValue(SlideFromRightProperty, value); }

    /// <summary>移植原控件的动画参数，复用 Storyboard 以避免每次换项重新构造动画。</summary>
    public HomeHeroCard()
    {
        InitializeComponent();
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);
        _shadow = new CarouselArtworkShadow(Artwork, ShadowHost);
        ActionPanel.RenderTransform = _textTransform;
        // DevWinUI 10.8.0 StoreCarousel.RunTextAnimation：整组、无错峰、无附加 easing。
        AddTransition(_textTransform, "TranslateX", 100, 0);
        AddTransition(ActionPanel, "Opacity", 0, 1);
        AddTransition(_textTransform, "ScaleX", .8, 1);
        AddTransition(_textTransform, "ScaleY", .8, 1);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _shadow.Attach();
        Artwork.ArtworkColorChanged += UpdateTone;
        UpdateTone(Artwork.ArtworkColor);
        PlayTextTransition();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _shadow.Dispose();
        Artwork.ArtworkColorChanged -= UpdateTone;
        ResetText();
    }
    private void AddTransition(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = StoreCarouselImageMotion.Duration,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        _textTransition.Children.Add(animation);
    }
    private static void ThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (HomeHeroCard)sender;
        // 直接适配可空主题到内部封面；x:Bind 函数的空中间路径会跳过调用。
        card.Artwork.Music = card.Theme?.Artwork;
        if (card.IsLoaded) card.PlayTextTransition();
    }
    private void ResetText()
    {
        _textTransition.Stop();
        ActionPanel.Opacity = 1;
        _textTransform.TranslateX = 0;
        _textTransform.ScaleX = 1;
        _textTransform.ScaleY = 1;
    }
    private void PlayTextTransition()
    {
        ResetText();
        ActionPanel.Visibility = Theme is null ? Visibility.Collapsed : Visibility.Visible;
        if (Theme is not null && _settings.AnimationsEnabled) _textTransition.Begin();
    }
    private double _measureWidth = double.NaN;
    private double _measureHeight = double.NaN;
    /// <inheritdoc />
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        WinUIMusicPlayer.Services.UiLayoutDiagnostics.Measure(this, availableSize);
        // 响应父级约束，不在 SizeChanged 中用舍入后的 ActualSize 反写并重新触发布局。
        if (double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height) &&
            (_measureWidth != availableSize.Width || _measureHeight != availableSize.Height))
        {
            double width = availableSize.Width;
            double height = availableSize.Height;
            bool compact = width < 360 || height < 220;
            double margin = compact ? 12 : 40;
            ActionPanel.Margin = compact ? new Thickness(12) : new Thickness(40, 24, 24, 24);
            ActionPanel.MaxWidth = System.Math.Min(300, System.Math.Max(0, width - margin - (compact ? 12 : 24)));
            TitleText.FontSize = compact ? 20 : 28;
            TitleText.MaxLines = height < 170 ? 1 : 2;
            DescriptionText.Visibility = height < 260 ? Visibility.Collapsed : Visibility.Visible;
            PlayButton.MinWidth = System.Math.Min(120, ActionPanel.MaxWidth);
            PlayButton.MaxWidth = ActionPanel.MaxWidth;
            _measureWidth = width;
            _measureHeight = height;
        }
        return base.MeasureOverride(availableSize);
    }
    private void UpdateTone(Color color)
    {
        if (color.A == 0) color = Color.FromArgb(255, 32, 32, 32);
        // 遮罩颜色由主题资源决定，不再取封面色（亮封面会让白色叠字失去可读性）；仅播放按钮沿用压暗后的封面色调。
        ActionBrush.Color = Color.FromArgb(255, (byte)(color.R * .5), (byte)(color.G * .5), (byte)(color.B * .5));
        ActionBorderBrush.Color = color;
        ActionHoverBrush.Color = Color.FromArgb(255, (byte)(color.R * .65), (byte)(color.G * .65), (byte)(color.B * .65));
        ActionPressedBrush.Color = Color.FromArgb(255, (byte)(color.R * .32), (byte)(color.G * .32), (byte)(color.B * .32));
    }
}
