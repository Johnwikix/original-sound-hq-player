using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

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
    /// <summary>标识拼贴点击命令。</summary>
    public static readonly DependencyProperty ThumbnailCommandProperty = DependencyProperty.Register(
        nameof(ThumbnailCommand), typeof(ICommand), typeof(FeaturedCarousel), new PropertyMetadata(null));
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
    private int _index;
    private bool _slideFromRight = true;
    private bool _pointerInside;
    private bool _focusInside;
    private readonly CarouselLighting _lighting;
    /// <summary>获取或设置主轮播数据，支持整体替换与集合通知。</summary>
    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>获取或设置复用的主内容视图。</summary>
    public UIElement? MainContent { get => (UIElement?)GetValue(MainContentProperty); set => SetValue(MainContentProperty, value); }
    /// <summary>获取或设置右侧或下方的拼贴数据。</summary>
    public IList? ThumbnailItemsSource { get => (IList?)GetValue(ThumbnailItemsSourceProperty); set => SetValue(ThumbnailItemsSourceProperty, value); }
    /// <summary>获取或设置拼贴模板。</summary>
    public DataTemplate? ThumbnailTemplate { get => (DataTemplate?)GetValue(ThumbnailTemplateProperty); set => SetValue(ThumbnailTemplateProperty, value); }
    /// <summary>获取或设置拼贴点击命令。</summary>
    public ICommand? ThumbnailCommand { get => (ICommand?)GetValue(ThumbnailCommandProperty); set => SetValue(ThumbnailCommandProperty, value); }
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
    /// <summary>获取与原控件一致的直接显隐状态，同时保留键盘聚焦入口。</summary>
    public Visibility NavigationVisibility => CanNavigate && (_pointerInside || _focusInside) ? Visibility.Visible : Visibility.Collapsed;
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
        PrimaryThumbnail.Translation = new Vector3(0, 0, 12);
        SecondaryThumbnail.Translation = new Vector3(0, 0, 12);
        TertiaryThumbnail.Translation = new Vector3(0, 0, 12);
        // Composition 的非环境光数量受限，整组封面复用一盏聚光处理悬停和按压。
        _lighting = new CarouselLighting(LayoutGrid);
        _shuffleTimer = DispatcherQueue.CreateTimer();
        _shuffleTimer.Tick += (sender, args) => Move(1);
    }
    /// <summary>按相对方向循环翻页。</summary>
    public void Move(int direction)
    {
        if (PageCount < 2) return;
        Select((_index + (direction < 0 ? -1 : 1) + PageCount) % PageCount);
    }
    private void Select(int index)
    {
        _slideFromRight = index > _index;
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
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ObserveCollections();
        NotifySelection();
        NotifyThumbnails();
        _lighting.Attach();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnobserveCollections();
        _pointerInside = false;
        _focusInside = false;
        _shuffleTimer.Stop();
        _lighting.Detach();
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
        UpdateLayoutMode();
    }
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateLayoutMode();
    }
    private void UpdateLayoutMode()
    {
        if (LayoutGrid is null) return;
        bool split = ThumbnailVisibility == Visibility.Visible;
        double width = LayoutGrid.ActualWidth;
        double mainWidth = split ? Math.Max(0, width - LayoutGrid.ColumnSpacing) * .6 : width;
        // 主图约 1.22:1；拼贴上图略宽、下图接近方形，随可用宽度等比调整。
        LayoutGrid.RowDefinitions[0].Height = new GridLength(mainWidth / 1.22);
        LayoutGrid.ColumnDefinitions[0].Width = new GridLength(split ? 1.5 : 1, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[1].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(SliderGrid, split ? 1 : 2);
        Grid.SetColumnSpan(Pager, split ? 1 : 2);
        bool largeTileOnly = SecondaryItem is null;
        Grid.SetColumn(PrimaryThumbnail, 0);
        Grid.SetRow(PrimaryThumbnail, 0);
        Grid.SetColumnSpan(PrimaryThumbnail, 2);
        Grid.SetRowSpan(PrimaryThumbnail, largeTileOnly ? 2 : 1);
        Grid.SetColumn(SecondaryThumbnail, 0);
        Grid.SetRow(SecondaryThumbnail, 1);
        Grid.SetColumnSpan(SecondaryThumbnail, TertiaryItem is null ? 2 : 1);
        Grid.SetColumn(TertiaryThumbnail, 1);
        Grid.SetRow(TertiaryThumbnail, 1);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyNavigation()
    {
        Notify(nameof(NavigationVisibility));
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
        // 原版 OnSliderPointerEntered：悬停期间暂停自动轮播并保留剩余时间。
        if (_shuffleTimer.IsRunning)
        {
            var remaining = _shuffleTimer.Interval - (DateTime.UtcNow - _shuffleStartedUtc);
            _shufflePausedRemaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            _shuffleTimer.Stop();
        }
        NotifyNavigation();
    }
    private void Carousel_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
        ResumeShuffle();
        NotifyNavigation();
    }
    private void Carousel_GotFocus(object sender, RoutedEventArgs args)
    {
        _focusInside = true;
        NotifyNavigation();
    }
    private void Carousel_LostFocus(object sender, RoutedEventArgs args)
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null && !ReferenceEquals(focused, this)) focused = VisualTreeHelper.GetParent(focused);
        _focusInside = focused is not null;
        NotifyNavigation();
    }
    private static void ShuffleSettingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (FeaturedCarousel)sender;
        control.RestartShuffle();
    }
    private bool ShouldShuffle => AutoShuffle && !_pointerInside && IsLoaded && PageCount > 1;
    /// <summary>对齐原版 StartTransition：每次换项或设置变化都重置完整间隔；悬停、卸载或不足两页时完全停住。</summary>
    private void RestartShuffle()
    {
        _shufflePausedRemaining = TimeSpan.Zero;
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
    }
    /// <summary>以暂停时剩余的间隔单次触发，随后由换项重启回到完整间隔，等价原版 PausableDispatcherTimer。</summary>
    private void ResumeShuffle()
    {
        var remaining = _shufflePausedRemaining;
        _shufflePausedRemaining = TimeSpan.Zero;
        if (remaining <= TimeSpan.Zero || !ShouldShuffle)
        {
            RestartShuffle();
            return;
        }
        _shuffleTimer.Stop();
        _shuffleTimer.IsRepeating = false;
        _shuffleTimer.Interval = remaining;
        _shuffleStartedUtc = DateTime.UtcNow;
        _shuffleTimer.Start();
    }
    private void Carousel_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        Move(args.Key == Windows.System.VirtualKey.Left ? -1 : 1);
        args.Handled = true;
    }
}
