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
    /// <summary>标识主内容模板。</summary>
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(FeaturedCarousel), new PropertyMetadata(null));
    /// <summary>标识独立的拼贴数据源。</summary>
    public static readonly DependencyProperty ThumbnailItemsSourceProperty = DependencyProperty.Register(
        nameof(ThumbnailItemsSource), typeof(IList), typeof(FeaturedCarousel), new PropertyMetadata(null, ThumbnailsChanged));
    /// <summary>标识拼贴模板。</summary>
    public static readonly DependencyProperty ThumbnailTemplateProperty = DependencyProperty.Register(
        nameof(ThumbnailTemplate), typeof(DataTemplate), typeof(FeaturedCarousel), new PropertyMetadata(null));
    /// <summary>标识拼贴点击命令。</summary>
    public static readonly DependencyProperty ThumbnailCommandProperty = DependencyProperty.Register(
        nameof(ThumbnailCommand), typeof(ICommand), typeof(FeaturedCarousel), new PropertyMetadata(null));
    private INotifyCollectionChanged? _observed;
    private INotifyCollectionChanged? _thumbnailObserved;
    private int _index;
    private bool _pointerInside;
    private bool _focusInside;
    private bool _wide;
    private readonly CarouselLighting _lighting;
    /// <summary>获取或设置主轮播数据，支持整体替换与集合通知。</summary>
    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>获取或设置主内容模板。</summary>
    public DataTemplate? ItemTemplate { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    /// <summary>获取或设置右侧或下方的拼贴数据。</summary>
    public IList? ThumbnailItemsSource { get => (IList?)GetValue(ThumbnailItemsSourceProperty); set => SetValue(ThumbnailItemsSourceProperty, value); }
    /// <summary>获取或设置拼贴模板。</summary>
    public DataTemplate? ThumbnailTemplate { get => (DataTemplate?)GetValue(ThumbnailTemplateProperty); set => SetValue(ThumbnailTemplateProperty, value); }
    /// <summary>获取或设置拼贴点击命令。</summary>
    public ICommand? ThumbnailCommand { get => (ICommand?)GetValue(ThumbnailCommandProperty); set => SetValue(ThumbnailCommandProperty, value); }
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
    /// <summary>获取多项轮播的翻页按钮可见性；悬停显隐由透明度过渡处理。</summary>
    public Visibility NavigationVisibility => CanNavigate ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>获取鼠标或键盘操作时的翻页按钮透明度。</summary>
    public double NavigationOpacity => IsNavigationInteractive ? 1 : 0;
    /// <summary>获取翻页按钮显示时的缩放。</summary>
    public Vector3 NavigationScale => IsNavigationInteractive ? Vector3.One : new Vector3(.9f);
    /// <summary>获取是否允许指针点击翻页按钮；键盘聚焦会先显示按钮。</summary>
    public bool IsNavigationInteractive => CanNavigate && (_pointerInside || _focusInside);
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>初始化 Store 布局，不启动后台计时器。</summary>
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
    }
    /// <summary>按相对方向循环翻页。</summary>
    public void Move(int direction)
    {
        if (PageCount == 0) return;
        _index = (_index + (direction < 0 ? -1 : 1) + PageCount) % PageCount;
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
        Notify(nameof(SelectedItem));
        Notify(nameof(SelectedIndex));
        Notify(nameof(CanNavigate));
        Notify(nameof(Position));
        Notify(nameof(PagerVisibility));
        NotifyNavigation();
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
        _wide = args.NewSize.Width >= 960;
        UpdateLayoutMode();
    }
    private void UpdateLayoutMode()
    {
        if (LayoutGrid is null) return;
        bool hasThumbnails = ThumbnailVisibility == Visibility.Visible;
        bool split = _wide && hasThumbnails;
        double width = LayoutGrid.ActualWidth;
        double mainWidth = split ? Math.Max(0, width - LayoutGrid.ColumnSpacing) * .6 : width;
        // 主图约 1.22:1；拼贴上图略宽、下图接近方形，随可用宽度等比调整。
        LayoutGrid.RowDefinitions[0].Height = new GridLength(mainWidth / 1.22);
        LayoutGrid.ColumnDefinitions[0].Width = new GridLength(split ? 1.5 : 1, GridUnitType.Star);
        LayoutGrid.ColumnDefinitions[1].Width = _wide && hasThumbnails ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(SliderGrid, _wide && hasThumbnails ? 1 : 2);
        Grid.SetColumnSpan(Pager, _wide && hasThumbnails ? 1 : 2);
        LayoutGrid.RowDefinitions[2].Height = new GridLength(!_wide && hasThumbnails ? width * .58 : 0);
        Grid.SetRow(ThumbnailGrid, _wide ? 0 : 2);
        Grid.SetColumn(ThumbnailGrid, _wide ? 1 : 0);
        Grid.SetColumnSpan(ThumbnailGrid, _wide ? 1 : 2);
        bool largeTileOnly = SecondaryItem is null;
        ThumbnailGrid.ColumnDefinitions[0].Width = new GridLength(_wide ? 1 : 1.7, GridUnitType.Star);
        ThumbnailGrid.RowDefinitions[0].Height = new GridLength(_wide ? 1.35 : 1, GridUnitType.Star);
        Grid.SetColumn(PrimaryThumbnail, 0);
        Grid.SetRow(PrimaryThumbnail, 0);
        Grid.SetColumnSpan(PrimaryThumbnail, _wide || largeTileOnly ? 2 : 1);
        Grid.SetRowSpan(PrimaryThumbnail, _wide && !largeTileOnly ? 1 : 2);
        Grid.SetColumn(SecondaryThumbnail, _wide ? 0 : 1);
        Grid.SetRow(SecondaryThumbnail, _wide ? 1 : 0);
        Grid.SetColumnSpan(SecondaryThumbnail, _wide && TertiaryItem is null ? 2 : 1);
        Grid.SetColumn(TertiaryThumbnail, 1);
        Grid.SetRow(TertiaryThumbnail, 1);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyNavigation()
    {
        Notify(nameof(NavigationVisibility));
        Notify(nameof(NavigationOpacity));
        Notify(nameof(NavigationScale));
        Notify(nameof(IsNavigationInteractive));
    }
    private void Previous_Click(object sender, RoutedEventArgs args) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs args) => Move(1);
    private void Pager_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (sender.SelectedPageIndex < 0 || sender.SelectedPageIndex >= PageCount || sender.SelectedPageIndex == _index) return;
        _index = sender.SelectedPageIndex;
        NotifySelection();
    }
    private void Carousel_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = true;
        NotifyNavigation();
    }
    private void Carousel_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerInside = false;
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
    private void Carousel_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        Move(args.Key == Windows.System.VirtualKey.Left ? -1 : 1);
        args.Handled = true;
    }
}
