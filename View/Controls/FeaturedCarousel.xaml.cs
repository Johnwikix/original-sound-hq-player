using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>复现 Store Carousel 的大图、叠字、三图拼贴及圆点分页，图片由调用方模板提供。</summary>
public sealed partial class FeaturedCarousel : UserControl, INotifyPropertyChanged
{
    /// <summary>标识主轮播数据源。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList), typeof(FeaturedCarousel), new PropertyMetadata(null, SourceChanged));
    /// <summary>标识稳定的主内容视图，换项时保留旧图片以执行交叠过渡。</summary>
    public static readonly DependencyProperty MainContentProperty = DependencyProperty.Register(
        nameof(MainContent), typeof(UIElement), typeof(FeaturedCarousel), new PropertyMetadata(null));
    /// <summary>标识独立的拼贴数据源。</summary>
    public static readonly DependencyProperty ThumbnailItemsSourceProperty = DependencyProperty.Register(
        nameof(ThumbnailItemsSource), typeof(IList), typeof(FeaturedCarousel), new PropertyMetadata(null, ThumbnailsChanged));
    /// <summary>标识拼贴模板。</summary>
    public static readonly DependencyProperty ThumbnailTemplateProperty = DependencyProperty.Register(
        nameof(ThumbnailTemplate), typeof(DataTemplate), typeof(FeaturedCarousel), new PropertyMetadata(null));
    /// <summary>标识第一张大拼贴的独立模板，未设置时沿用通用模板。</summary>
    public static readonly DependencyProperty PrimaryThumbnailTemplateProperty = DependencyProperty.Register(
        nameof(PrimaryThumbnailTemplate), typeof(DataTemplate), typeof(FeaturedCarousel), new PropertyMetadata(null));
    /// <summary>标识是否自动轮播，沿用原版 StoreCarousel.AutoShuffle 默认开启。</summary>
    public static readonly DependencyProperty AutoShuffleProperty = DependencyProperty.Register(
        nameof(AutoShuffle), typeof(bool), typeof(FeaturedCarousel), new PropertyMetadata(true, ShuffleSettingChanged));
    /// <summary>标识自动轮播间隔，沿用原版 StoreCarousel.ShuffleDuration 默认 5 秒。</summary>
    public static readonly DependencyProperty ShuffleDurationProperty = DependencyProperty.Register(
        nameof(ShuffleDuration), typeof(TimeSpan), typeof(FeaturedCarousel),
        new PropertyMetadata(TimeSpan.FromSeconds(5), ShuffleSettingChanged));
    private INotifyCollectionChanged? _observed;
    private INotifyCollectionChanged? _thumbnailObserved;
    private readonly DispatcherQueueTimer _shuffleTimer;
    private DateTime _shuffleStartedUtc;
    private TimeSpan _shufflePausedRemaining;
    private double _countdownFraction = 1;
    private int _index;
    private bool _slideFromRight = true;
    private bool _pointerInside;
    private bool _focusInside;
    private readonly CarouselLighting _lighting;
    private readonly CarouselArtworkShadow[] _thumbnailShadows;
    /// <summary>获取或设置主轮播数据，支持整体替换与集合通知。</summary>
    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>获取或设置复用的主内容视图。</summary>
    public UIElement? MainContent { get => (UIElement?)GetValue(MainContentProperty); set => SetValue(MainContentProperty, value); }
    /// <summary>获取或设置右侧或下方的拼贴数据。</summary>
    public IList? ThumbnailItemsSource { get => (IList?)GetValue(ThumbnailItemsSourceProperty); set => SetValue(ThumbnailItemsSourceProperty, value); }
    /// <summary>获取或设置拼贴模板。</summary>
    public DataTemplate? ThumbnailTemplate { get => (DataTemplate?)GetValue(ThumbnailTemplateProperty); set => SetValue(ThumbnailTemplateProperty, value); }
    /// <summary>获取或设置第一张大拼贴模板。</summary>
    public DataTemplate? PrimaryThumbnailTemplate { get => (DataTemplate?)GetValue(PrimaryThumbnailTemplateProperty); set => SetValue(PrimaryThumbnailTemplateProperty, value); }
    /// <summary>选择大拼贴模板并保留原调用方兼容性。</summary>
    public DataTemplate? ResolvePrimaryTemplate(DataTemplate? primary, DataTemplate? fallback) => primary ?? fallback;
    /// <summary>获取或设置是否自动轮播。</summary>
    public bool AutoShuffle { get => (bool)GetValue(AutoShuffleProperty); set => SetValue(AutoShuffleProperty, value); }
    /// <summary>获取或设置自动轮播间隔。</summary>
    public TimeSpan ShuffleDuration { get => (TimeSpan)GetValue(ShuffleDurationProperty); set => SetValue(ShuffleDurationProperty, value); }
    /// <summary>获取当前主轮播项。</summary>
    public object? SelectedItem => ItemsSource is { Count: > 0 } items ? items[_index] : null;
    /// <summary>获取当前页索引。</summary>
    public int SelectedIndex => _index;
    /// <summary>获取主轮播页数。</summary>
    public int PageCount => ItemsSource?.Count ?? 0;
    /// <summary>获取是否允许主轮播翻页。</summary>
    public bool CanNavigate => PageCount > 1;
    /// <summary>获取当前页的文字表示。</summary>
    public string Position => PageCount > 0 ? $"{_index + 1} / {PageCount}" : "0 / 0";
    /// <summary>获取第一张拼贴；跳过与主图通常相同的首项。</summary>
    public object? PrimaryItem => ThumbnailAt(0);
    /// <summary>获取第二张拼贴。</summary>
    public object? SecondaryItem => ThumbnailAt(1);
    /// <summary>获取第三张拼贴。</summary>
    public object? TertiaryItem => ThumbnailAt(2);
    /// <summary>获取拼贴区域可见性。</summary>
    public Visibility ThumbnailVisibility => PrimaryItem is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取第二张拼贴可见性。</summary>
    public Visibility SecondaryVisibility => SecondaryItem is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取第三张拼贴可见性。</summary>
    public Visibility TertiaryVisibility => TertiaryItem is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取分页圆点可见性。</summary>
    public Visibility PagerVisibility => CanNavigate ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取箭头容器可见性；有多页时保留容器以完成淡出。</summary>
    public Visibility NavigationVisibility => CanNavigate ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取箭头是否接受输入，鼠标焦点不会使箭头在移出后常驻。</summary>
    public bool NavigationActive => IsLoaded && CanNavigate && (_pointerInside || _focusInside);
    /// <summary>获取箭头目标透明度，由 XAML 原生过渡完成淡入淡出。</summary>
    public double NavigationOpacity => NavigationActive ? 1 : 0;
    /// <summary>获取自动轮播倒计时圆盘的可见性。</summary>
    public Visibility CountdownVisibility => IsLoaded && AutoShuffle && CanNavigate ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取是否使用原控件的 SlideLeft，即从右侧滑入；反向切换从左侧滑入。</summary>
    public bool SlideFromRight => _slideFromRight;
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>初始化 Store 布局并准备自动轮播计时器，实际启停跟随加载状态。</summary>
    public FeaturedCarousel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Composition 的非环境光数量受限，整组封面复用一盏聚光处理悬停和按压。
        _lighting = new CarouselLighting(LayoutGrid, Presenter, PrimaryThumbnail, SecondaryThumbnail, TertiaryThumbnail);
        _thumbnailShadows = [new(PrimaryThumbnail, PrimaryShadowHost), new(SecondaryThumbnail, SecondaryShadowHost), new(TertiaryThumbnail, TertiaryShadowHost)];
        _shuffleTimer = DispatcherQueue.CreateTimer();

    }
    /// <summary>按相对方向循环翻页。</summary>
    public void Move(int direction)
    {
        if (PageCount < 2) return;
        Select((_index + (direction < 0 ? -1 : 1) + PageCount) % PageCount, direction > 0);
    }
    private void Select(int index, bool? fromRight = null)
    {
        _slideFromRight = fromRight ?? index > _index;
        _index = index;
        NotifySelection();
    }
    private object? ThumbnailAt(int offset)
    {
        if (ThumbnailItemsSource is not { Count: > 0 } items) return null;
        int first = items.Count > 3 ? 1 : 0;
        int index = first + offset;
        return index < items.Count ? items[index] : null;
    }
    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (FeaturedCarousel)sender;
        control._index = 0;
        control._slideFromRight = true;
        control.ObserveCollections();
        control.NotifySelection();
    }
    private static void ThumbnailsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (FeaturedCarousel)sender;
        control.ObserveCollections();
        control.NotifyThumbnails();
    }
    private void ShuffleTimer_Tick(DispatcherQueueTimer sender, object args) => Move(1);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _shuffleTimer.Tick -= ShuffleTimer_Tick;
        _shuffleTimer.Tick += ShuffleTimer_Tick;
        ObserveCollections();
        NotifySelection();
        NotifyThumbnails();
        UpdateNavigationLayout();
        _lighting.Attach();
        foreach (var shadow in _thumbnailShadows) shadow.Attach();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnobserveCollections();
        _pointerInside = false;
        _focusInside = false;
        _shuffleTimer.Stop();
        _shuffleTimer.Tick -= ShuffleTimer_Tick;
        Countdown.StopAt(0);
        NotifyNavigation();
        Notify(nameof(CountdownVisibility));
        _lighting.Detach();
        foreach (var shadow in _thumbnailShadows) shadow.Dispose();
    }
    private void UnobserveCollections()
    {
        if (_observed is not null) _observed.CollectionChanged -= CollectionChanged;
        if (_thumbnailObserved is not null) _thumbnailObserved.CollectionChanged -= ThumbnailCollectionChanged;
        _observed = null;
        _thumbnailObserved = null;
    }
    private void ObserveCollections()
    {
        UnobserveCollections();
        if (!IsLoaded) return;
        _observed = ItemsSource as INotifyCollectionChanged;
        _thumbnailObserved = ThumbnailItemsSource as INotifyCollectionChanged;
        if (_observed is not null) _observed.CollectionChanged += CollectionChanged;
        if (_thumbnailObserved is not null) _thumbnailObserved.CollectionChanged += ThumbnailCollectionChanged;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => NotifySelection();
    private void ThumbnailCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => NotifyThumbnails();
    private void NotifySelection()
    {
        _index = Math.Clamp(_index, 0, Math.Max(0, PageCount - 1));
        Notify(nameof(PageCount));
        Notify(nameof(SlideFromRight));
        Notify(nameof(SelectedItem));
        Notify(nameof(SelectedIndex));
        Notify(nameof(CanNavigate));
        Notify(nameof(Position));
        Notify(nameof(PagerVisibility));
        NotifyNavigation();
        RestartShuffle();
    }
    private void NotifyThumbnails()
    {
        Notify(nameof(PrimaryItem));
        Notify(nameof(SecondaryItem));
        Notify(nameof(TertiaryItem));
        Notify(nameof(ThumbnailVisibility));
        Notify(nameof(SecondaryVisibility));
        Notify(nameof(TertiaryVisibility));
        InvalidateMeasure();
    }
    /// <inheritdoc />
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        if (LayoutGrid is not null && double.IsFinite(availableSize.Width))
        {
            // 使用父级分配的测量宽度；ActualWidth 已经经过 DPI 舍入，反写行列会形成一像素的布局循环。
            double width = Math.Max(0, availableSize.Width - LayoutGrid.Margin.Left - LayoutGrid.Margin.Right);
            UpdateLayoutMode(width);
        }
        return base.MeasureOverride(availableSize);
    }
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateNavigationLayout();
    private void Slider_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateNavigationLayout();
    private void UpdateNavigationLayout()
    {
        if (!SliderGrid.IsLoaded || SliderGrid.ActualWidth <= 0) return;
        var origin = SliderGrid.TransformToVisual(NavigationOverlay).TransformPoint(new Windows.Foundation.Point());
        double top = origin.Y + (SliderGrid.ActualHeight - 32) / 2;
        Canvas.SetLeft(PreviousButton, origin.X - 10);
        Canvas.SetLeft(NextButton, origin.X + SliderGrid.ActualWidth - 22);
        Canvas.SetTop(PreviousButton, top);
        Canvas.SetTop(NextButton, top);
    }
    private void UpdateLayoutMode(double width)
    {
        const double aspectRatio = 16d / 9;
        bool split = ThumbnailVisibility == Visibility.Visible;
        bool lowerRow = SecondaryItem is not null;
        bool lowerPair = TertiaryItem is not null;
        double rowGap = lowerRow ? ThumbnailGrid.ColumnSpacing : 0;
        double mainWidth = width;
        double thumbnailWidth = 0;
        if (split)
        {
            // 主图与右侧拼贴等高，各卡片均为 16:9；下方半宽卡片和行间距一并计入。
            double lowerWidthFraction = lowerPair ? .5 : lowerRow ? 1 : 0;
            double halfColumnGap = lowerPair ? ThumbnailGrid.ColumnSpacing / 2 : 0;
            thumbnailWidth = Math.Max(0, (width - LayoutGrid.ColumnSpacing - aspectRatio * rowGap + halfColumnGap)
                / (2 + lowerWidthFraction));
            mainWidth = Math.Max(0, width - LayoutGrid.ColumnSpacing - thumbnailWidth);
        }
        LayoutGrid.RowDefinitions[0].Height = new GridLength(mainWidth / aspectRatio);
        // 分栏时主图使用分配宽度，避免图片的固有尺寸改变 Star 列的最小期望宽度和舍入结果。
        LayoutGrid.ColumnDefinitions[0].Width = split ? new GridLength(mainWidth) : new GridLength(1, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ThumbnailGrid.RowSpacing = rowGap;
        ThumbnailGrid.RowDefinitions[0].Height = new GridLength(thumbnailWidth / aspectRatio);
        ThumbnailGrid.RowDefinitions[1].Height = lowerRow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(SliderGrid, split ? 1 : 2);
        Grid.SetColumnSpan(Pager, split ? 1 : 2);
        bool largeTileOnly = SecondaryItem is null;
        Grid.SetColumn(PrimaryThumbnail, 0);
        Grid.SetRow(PrimaryThumbnail, 0);
        Grid.SetColumnSpan(PrimaryThumbnail, 2);
        Grid.SetRowSpan(PrimaryThumbnail, largeTileOnly ? 2 : 1);
        Grid.SetRowSpan(PrimaryShadowHost, largeTileOnly ? 2 : 1);
        Grid.SetColumn(SecondaryThumbnail, 0);
        Grid.SetRow(SecondaryThumbnail, 1);
        Grid.SetColumnSpan(SecondaryThumbnail, TertiaryItem is null ? 2 : 1);
        Grid.SetColumnSpan(SecondaryShadowHost, TertiaryItem is null ? 2 : 1);
        Grid.SetColumn(TertiaryThumbnail, 1);
        Grid.SetRow(TertiaryThumbnail, 1);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyNavigation()
    {
        Notify(nameof(NavigationVisibility));
        Notify(nameof(NavigationActive));
        Notify(nameof(NavigationOpacity));
    }
    private void Previous_Click(object sender, RoutedEventArgs args) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs args) => Move(1);
    private void Pager_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (sender.SelectedPageIndex < 0 || sender.SelectedPageIndex >= PageCount || sender.SelectedPageIndex == _index) return;
        Select(sender.SelectedPageIndex);
    }
    private void Carousel_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = true;
        PauseShuffle();
        NotifyNavigation();
    }
    private void PauseShuffle()
    {
        if (_shuffleTimer.IsRunning)
        {
            var remaining = _shuffleTimer.Interval - (DateTime.UtcNow - _shuffleStartedUtc);
            _shufflePausedRemaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            _countdownFraction *= Math.Clamp(_shufflePausedRemaining.TotalMilliseconds / _shuffleTimer.Interval.TotalMilliseconds, 0, 1);
            _shuffleTimer.Stop();
            Countdown.StopAt(_countdownFraction);
        }
    }
    private void Carousel_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
        _focusInside = KeyboardFocusInside();
        ResumeShuffle();
        NotifyNavigation();
    }
    private void Carousel_GotFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = KeyboardFocusInside();
        if (_focusInside) PauseShuffle();
        NotifyNavigation();
    }
    private void Carousel_LostFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = KeyboardFocusInside();
        if (!_focusInside) ResumeShuffle();
        NotifyNavigation();
    }
    private bool KeyboardFocusInside()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not Control { FocusState: FocusState.Keyboard } control) return false;
        DependencyObject? focused = control;
        while (focused is not null && !ReferenceEquals(focused, this)) focused = VisualTreeHelper.GetParent(focused);
        return focused is not null;
    }
    private static void ShuffleSettingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (FeaturedCarousel)sender;
        control.RestartShuffle();
    }
    private bool ShouldShuffle => AutoShuffle && !_pointerInside && !_focusInside && IsLoaded && PageCount > 1;
    /// <summary>对齐原版 StartTransition：每次换项或设置变化都重置完整间隔；悬停、卸载或不足两页时完全停住。</summary>
    private void RestartShuffle()
    {
        _shufflePausedRemaining = TimeSpan.Zero;
        _countdownFraction = 1;
        Countdown.StopAt(1);
        Notify(nameof(CountdownVisibility));
        if (!ShouldShuffle)
        {
            _shuffleTimer.Stop();
            return;
        }
        var interval = ShuffleDuration > TimeSpan.Zero ? ShuffleDuration : TimeSpan.FromSeconds(5);
        _shuffleTimer.Stop();
        _shuffleTimer.IsRepeating = true;
        _shuffleTimer.Interval = interval;
        _shuffleStartedUtc = DateTime.UtcNow;
        _shuffleTimer.Start();
        Countdown.Run(1, interval);
    }
    /// <summary>以暂停时剩余的间隔单次触发，随后由换项重启回到完整间隔，等价原版 PausableDispatcherTimer。</summary>
    private void ResumeShuffle()
    {
        if (!ShouldShuffle) return;
        var remaining = _shufflePausedRemaining;
        _shufflePausedRemaining = TimeSpan.Zero;
        if (remaining <= TimeSpan.Zero)
        {
            RestartShuffle();
            return;
        }
        _shuffleTimer.Stop();
        _shuffleTimer.IsRepeating = false;
        _shuffleTimer.Interval = remaining;
        _shuffleStartedUtc = DateTime.UtcNow;
        _shuffleTimer.Start();
        Countdown.Run(_countdownFraction, remaining);
    }
    private void Carousel_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        Move(args.Key == Windows.System.VirtualKey.Left ? -1 : 1);
        args.Handled = true;
    }
}
