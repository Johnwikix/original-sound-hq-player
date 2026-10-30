using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>使用有边界状态的按钮按整张卡片翻页，滚轮留给页面，栏目仅由按钮和键盘导航移动。</summary>
public sealed partial class RecommendationShelf : UserControl
{
    /// <summary>标识栏目数据。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList), typeof(RecommendationShelf), new PropertyMetadata(null, SourceChanged));
    /// <summary>标识歌曲卡片模板。</summary>
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(RecommendationShelf), new PropertyMetadata(null));
    /// <summary>标识本地化的栏目标题视图。</summary>
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(RecommendationShelf), new PropertyMetadata(null));
    private double _requestedOffset;
    private int _scrollCorrelationId = -1;
    private readonly ScrollingScrollOptions _pageOptions = new(ScrollingAnimationMode.Auto);
    private readonly ScrollingScrollOptions _resetOptions = new(ScrollingAnimationMode.Disabled);
    /// <summary>获取或设置栏目曲目。</summary>
    public IList? ItemsSource { get => (IList?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>获取或设置歌曲模板。</summary>
    public DataTemplate? ItemTemplate { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    /// <summary>获取或设置栏目标题。</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    /// <summary>在翻页开始前通知页面收起悬浮详情。</summary>
    public event EventHandler? Paging;

    /// <summary>初始化栏目；禁用原生横向手势，程序翻页与焦点滚入仍可用。</summary>
    public RecommendationShelf()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }
    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var shelf = (RecommendationShelf)sender;
        shelf._requestedOffset = 0;
        shelf._scrollCorrelationId = -1;
        if (shelf.ShelfScroll is not null) shelf.ScrollToRequested(shelf._resetOptions);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) => _scrollCorrelationId = -1;
    private void ScrollToRequested(ScrollingScrollOptions options)
    {
        int correlationId = ShelfScroll.ScrollTo(_requestedOffset, 0, options);
        if (correlationId >= 0) _scrollCorrelationId = correlationId;
    }
    private void Scroll_ViewChanged(ScrollView sender, object args)
    {
        Paging?.Invoke(this, EventArgs.Empty);
        UpdateButtons();
    }
    private void Scroll_Completed(ScrollView sender, ScrollingScrollCompletedEventArgs args)
    {
        // 被后续翻页取代的完成通知不能覆盖快速连点的最新目标。
        if (args.CorrelationId == _scrollCorrelationId)
        {
            _requestedOffset = sender.HorizontalOffset;
            _scrollCorrelationId = -1;
        }
        UpdateButtons();
    }
    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateButtons();
    private void Scroll_ExtentChanged(ScrollView sender, object args) => UpdateButtons();
    private void UpdateButtons()
    {
        PreviousButton.IsEnabled = ShelfScroll.HorizontalOffset > .5;
        NextButton.IsEnabled = ShelfScroll.HorizontalOffset < ShelfScroll.ScrollableWidth - .5;
    }
    private void Previous_Click(object sender, RoutedEventArgs args) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs args) => Move(1);
    /// <summary>按视口能容纳的完整卡片数切换；快速点击在目标位置上累加。</summary>
    public void Move(int direction)
    {
        if (ShelfScroll.ScrollableWidth <= .5) return;
        Paging?.Invoke(this, EventArgs.Empty);
        const double stride = 172;
        double page = Math.Max(stride, Math.Floor(ShelfScroll.ViewportWidth / stride) * stride);
        double start = _scrollCorrelationId >= 0 ? _requestedOffset : ShelfScroll.HorizontalOffset;
        _requestedOffset = Math.Clamp(start + (direction < 0 ? -page : page), 0, ShelfScroll.ScrollableWidth);
        ScrollToRequested(_pageOptions);
    }
}
