using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>为目录列表的可见行提供短时增删淡入淡出，让位由原生 RepositionThemeTransition 负责。</summary>
internal sealed class FolderTreeMotion : IDisposable
{
    internal static readonly TimeSpan AddDuration = TimeSpan.FromMilliseconds(120);
    internal static readonly TimeSpan DeleteDuration = TimeSpan.FromMilliseconds(90);
    internal static readonly TimeSpan StaggerInterval = TimeSpan.FromMilliseconds(16);
    private const int StaggerSteps = 7;

    private readonly INotifyCollectionChanged _source;
    private readonly Dictionary<ListViewItem, object> _rows = [];
    private readonly HashSet<ListViewItem> _departing = [];
    private readonly Dictionary<object, int> _added = [];
    private readonly Dictionary<object, RowSnapshot> _snapshot = [];
    private readonly UISettings _settings = new();
    private ListView? _list;
    private Canvas? _overlay;
    private ScrollViewer? _scroll;
    private DispatcherQueueTimer? _timer;
    private readonly ScalarKeyFrameAnimation[] _show = new ScalarKeyFrameAnimation[StaggerSteps];
    private readonly ScalarKeyFrameAnimation[] _hide = new ScalarKeyFrameAnimation[StaggerSteps];
    private bool _animating;
    private bool _layoutPending;
    private int _firstChangedIndex;
    private int _removedCount;
    private double _scrollOffset;

    /// <summary>在 ListView 订阅数据源前开始观察，确保移除前就能给离场行安装动画。</summary>
    internal FolderTreeMotion(INotifyCollectionChanged source)
    {
        _source = source;
        _source.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>连接实际列表，并为本视图复用 Composition 动画及单个收尾计时器。</summary>
    internal void Attach(ListView list, Canvas overlay)
    {
        _list = list;
        _overlay = overlay;
        var compositor = ElementCompositionPreview.GetElementVisual(list).Compositor;
        for (int i = 0; i < StaggerSteps; i++)
        {
            var delay = StaggerInterval * i;
            var show = compositor.CreateScalarKeyFrameAnimation();
            show.Target = "Opacity";
            show.Duration = AddDuration;
            show.DelayTime = delay;
            show.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            show.InsertKeyFrame(0, 0);
            show.InsertKeyFrame(1, 1);
            _show[i] = show;
            var hide = compositor.CreateScalarKeyFrameAnimation();
            hide.Target = "Opacity";
            hide.Duration = DeleteDuration;
            hide.DelayTime = delay;
            hide.InsertKeyFrame(0, 1);
            hide.InsertKeyFrame(1, 0);
            _hide[i] = hide;
        }
        _timer = list.DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = AddDuration + StaggerInterval * (StaggerSteps - 1) + TimeSpan.FromMilliseconds(50);
        _timer.Tick += OnMotionFinished;
        list.ContainerContentChanging += OnContainerContentChanging;
        list.Loaded += OnLoaded;
        list.SizeChanged += OnSizeChanged;
        if (list.IsLoaded) ConnectScrollViewer();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => ConnectScrollViewer();

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        FinishMotion();
        UpdateClip();
    }

    private void ConnectScrollViewer()
    {
        if (_scroll is not null || _list is null) return;
        _scroll = FindScrollViewer(_list);
        if (_scroll is null) return;
        _scrollOffset = _scroll.VerticalOffset;
        _scroll.ViewChanging += OnViewChanging;
        UpdateClip();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void OnViewChanging(object? sender, ScrollViewerViewChangingEventArgs args)
    {
        if (Math.Abs(args.NextView.VerticalOffset - _scrollOffset) < 0.1) return;
        _scrollOffset = args.NextView.VerticalOffset;
        FinishMotion();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_list is null) return;
        if (args.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
            || !_settings.AnimationsEnabled)
        {
            FinishMotion();
            return;
        }

        // .NET 11 项目：只遍历已实现容器，复用动画/集合；不随整棵目录树创建逐项任务或闭包。
        if (!_layoutPending)
        {
            _firstChangedIndex = args.Action == NotifyCollectionChangedAction.Add
                ? args.NewStartingIndex : args.OldStartingIndex;
            _removedCount = 0;
            // 底层批量删除先提交最终数据再补发逐项通知；后续通知到来时，原容器可能已被移动/回收。
            foreach (var (row, item) in _rows)
            {
                var origin = row.TransformToVisual(_overlay).TransformPoint(default);
                _snapshot[item] = new(row, origin, row.ActualWidth, row.ActualHeight, row.IsSelected);
            }
            _layoutPending = true;
            _list.LayoutUpdated += OnLayoutUpdated;
        }
        _animating = true;
        if (args.NewItems is { } added)
        {
            for (int i = 0; i < added.Count; i++)
            {
                _added[added[i]!] = GetStaggerStep(args.NewStartingIndex + i - _firstChangedIndex);
                RemoveGhost(added[i]!);
            }
        }
        if (args.OldItems is { } removed)
        {
            for (int i = 0; i < removed.Count; i++)
            {
                var item = removed[i]!;
                _added.Remove(item);
                if (_snapshot.TryGetValue(item, out var snapshot))
                {
                    if (_rows.TryGetValue(snapshot.Row, out var current) && ReferenceEquals(current, item))
                    {
                        _departing.Add(snapshot.Row);
                        ResetOpacity(snapshot.Row);
                        ElementCompositionPreview.GetElementVisual(snapshot.Row).Opacity = 0;
                    }
                    AddGhost(snapshot, item, GetStaggerStep(_removedCount + i));
                }
            }
        }
        _removedCount += args.OldItems?.Count ?? 0;
        _timer!.Stop();
        _timer.Start();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem row) return;
        if (args.InRecycleQueue)
        {
            _rows.Remove(row);
            if (_departing.Contains(row))
            {
                // 回收通知可能早于原行真正离开渲染树；保持它隐藏，避免与淡出副本叠亮。
                var visual = ElementCompositionPreview.GetElementVisual(row);
                visual.StopAnimation("Opacity");
                visual.Opacity = 0;
            }
            else
            {
                ResetOpacity(row);
            }
            return;
        }
        if (args.Phase != 0) return;
        _departing.Remove(row);
        ResetOpacity(row);
        _rows[row] = args.Item;
        if (_animating && _added.Remove(args.Item, out int step))
            ElementCompositionPreview.GetElementVisual(row).StartAnimation("Opacity", _show[step]);
    }

    private static int GetStaggerStep(int index) => Math.Clamp(index, 0, StaggerSteps - 1);

    private void OnLayoutUpdated(object? sender, object args)
    {
        if (_list is not null) _list.LayoutUpdated -= OnLayoutUpdated;
        _layoutPending = false;
        _snapshot.Clear();
    }

    /// <summary>保存一批通知到来前的可见行几何与选中外观，不复制目录状态。</summary>
    private readonly record struct RowSnapshot(ListViewItem Row, Windows.Foundation.Point Origin,
        double Width, double Height, bool IsSelected);

    private void AddGhost(RowSnapshot snapshot, object item, int step)
    {
        if (_overlay is null || _list is null || snapshot.Height <= 0) return;
        var origin = snapshot.Origin;
        if (origin.Y + snapshot.Height <= 0 || origin.Y >= _overlay.ActualHeight) return;
        var row = snapshot.Row;
        UpdateClip();
        // ListView 会把回收行移出视口，原容器上的隐式 hide 无法显示；仅为可见离场行保留短暂的模板副本。
        var ghost = new ListViewItem
        {
            Content = item,
            ContentTemplate = _list.ItemTemplate,
            Style = _list.ItemContainerStyle,
            FontFamily = row.FontFamily,
            FontSize = row.FontSize,
            FontWeight = row.FontWeight,
            FontStyle = row.FontStyle,
            FontStretch = row.FontStretch,
            Foreground = row.Foreground,
            Width = snapshot.Width,
            Height = snapshot.Height,
            IsSelected = snapshot.IsSelected,
            IsTabStop = false,
            Tag = _hide[step]
        };
        AutomationProperties.SetAccessibilityView(ghost, AccessibilityView.Raw);
        Canvas.SetLeft(ghost, origin.X);
        Canvas.SetTop(ghost, origin.Y);
        ghost.Loaded += OnGhostLoaded;
        _overlay.Children.Add(ghost);
    }

    private static void OnGhostLoaded(object sender, RoutedEventArgs args)
    {
        var ghost = (ListViewItem)sender;
        ghost.Loaded -= OnGhostLoaded;
        if (ghost.Tag is ScalarKeyFrameAnimation hide)
            ElementCompositionPreview.GetElementVisual(ghost).StartAnimation("Opacity", hide);
    }

    private void UpdateClip()
    {
        if (_overlay is null) return;
        if (_overlay.Clip is not RectangleGeometry clip)
            _overlay.Clip = clip = new RectangleGeometry();
        clip.Rect = new Windows.Foundation.Rect(0, 0, _overlay.ActualWidth, _overlay.ActualHeight);
    }

    private void RemoveGhost(object item)
    {
        if (_overlay is null) return;
        for (int i = _overlay.Children.Count - 1; i >= 0; i--)
        {
            if (_overlay.Children[i] is not ListViewItem ghost || !ReferenceEquals(ghost.Content, item)) continue;
            ghost.Loaded -= OnGhostLoaded;
            _overlay.Children.RemoveAt(i);
        }
    }

    private void OnMotionFinished(DispatcherQueueTimer sender, object args) => FinishMotion();

    private void FinishMotion()
    {
        _timer?.Stop();
        _animating = false;
        if (_layoutPending) OnLayoutUpdated(null, EventArgs.Empty);
        _added.Clear();
        foreach (var row in _rows.Keys) ResetOpacity(row);
        foreach (var row in _departing) ResetOpacity(row);
        _departing.Clear();
        if (_overlay is not null)
        {
            foreach (ListViewItem ghost in _overlay.Children) ghost.Loaded -= OnGhostLoaded;
            _overlay.Children.Clear();
        }
    }

    private static void ResetOpacity(ListViewItem row)
    {
        var visual = ElementCompositionPreview.GetElementVisual(row);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }

    /// <summary>取消动画并解除数据源、容器、滚动和计时器订阅。</summary>
    public void Dispose()
    {
        _source.CollectionChanged -= OnCollectionChanged;
        FinishMotion();
        if (_list is not null)
        {
            _list.ContainerContentChanging -= OnContainerContentChanging;
            _list.Loaded -= OnLoaded;
            _list.SizeChanged -= OnSizeChanged;
            _list = null;
        }
        if (_scroll is not null)
        {
            _scroll.ViewChanging -= OnViewChanging;
            _scroll = null;
        }
        if (_timer is not null)
        {
            _timer.Tick -= OnMotionFinished;
            _timer = null;
        }
        _rows.Clear();
        _overlay = null;
        for (int i = 0; i < StaggerSteps; i++)
        {
            _show[i]?.Dispose();
            _show[i] = null!;
            _hide[i]?.Dispose();
            _hide[i] = null!;
        }
    }
}
