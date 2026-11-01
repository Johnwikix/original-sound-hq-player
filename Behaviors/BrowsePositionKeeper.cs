using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Behaviors;

public enum BrowsePositionKind { Song, Album, Artist, Folder }

/// <summary>视图拥有恢复工作；共享状态只接收标识和坐标，页面卸载取消工作并解绑全部外部订阅。</summary>
public sealed class BrowsePositionKeeper
{
    private static readonly ConditionalWeakTable<ListViewBase, Viewport> ActiveViews = new();

    internal static void InterruptRestore(ListViewBase list)
    {
        if (ActiveViews.TryGetValue(list, out var view)) view.InterruptRestore();
    }

    private readonly Page _page;
    private readonly BrowsePagePosition _position;
    private readonly SemanticZoom? _zoom;
    private readonly Viewport[] _views;
    private bool _initialZoom;
    private bool _loaded;
    private readonly PointerEventHandler _pointerIntent;
    private readonly KeyEventHandler _keyIntent;

    public BrowsePositionKeeper(Page page, BrowseState browse, BrowsePagePosition position,
        ListViewBase items, BrowsePositionKind kind = BrowsePositionKind.Song,
        SemanticZoom? zoom = null, ListViewBase? groups = null,
        ListViewBase? details = null, Func<string?>? detailIdentity = null)
    {
        _page = page;
        _position = position;
        _zoom = zoom;
        _pointerIntent = (_, _) => CancelRestore();
        _keyIntent = (_, _) => CancelRestore();
        int source = int.MinValue, detailSource = int.MinValue;
        string? search = null, sort = null, cached = null, detail = null, cachedDetail = null;
        string Context()
        {
            if (cached is not null && source == browse.SourceFilterId && search == browse.SearchText && sort == browse.SelectedSortOption?.Tag) return cached;
            source = browse.SourceFilterId;
            search = browse.SearchText;
            sort = browse.SelectedSortOption?.Tag;
            return cached = string.Concat(source.ToString(CultureInfo.InvariantCulture), "\u001f", search, "\u001f", sort);
        }
        string? DetailContext()
        {
            if (detailIdentity?.Invoke() is not { Length: > 0 } identity) return null;
            if (cachedDetail is not null && detail == identity && detailSource == browse.SourceFilterId) return cachedDetail;
            detail = identity;
            detailSource = browse.SourceFilterId;
            return cachedDetail = string.Concat(detailSource.ToString(CultureInfo.InvariantCulture), "\u001f", identity);
        }
        _views = groups is not null && details is not null
            ? [new(items, position.Items, Context, kind), new(groups, position.Groups, Context, kind),
                new(details, position.Details, DetailContext, BrowsePositionKind.Song)]
            : [new(items, position.Items, Context, kind)];
        page.Loaded += Loaded;
        page.Unloaded += Unloaded;
    }

    public void Leave()
    {
        if (!_loaded) return;
        if (_zoom is not null) _position.IsZoomedIn = _zoom.IsZoomedInViewActive;
        foreach (var view in _views)
        {
            view.Capture();
            view.Deactivate();
        }
    }

    public void CancelRestore()
    {
        foreach (var view in _views) view.InterruptRestore();
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        if (_zoom is not null)
        {
            _initialZoom = _zoom.IsZoomedInViewActive != _position.IsZoomedIn;
            _zoom.ViewChangeStarted += ZoomStarted;
            _zoom.ViewChangeCompleted += ZoomCompleted;
            _zoom.IsZoomedInViewActive = _position.IsZoomedIn;
        }
        _page.AddHandler(UIElement.PointerPressedEvent, _pointerIntent, true);
        _page.AddHandler(UIElement.PointerWheelChangedEvent, _pointerIntent, true);
        _page.AddHandler(UIElement.KeyDownEvent, _keyIntent, true);
        foreach (var view in _views) view.Activate();
    }

    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        // Unloaded 时容器已可能脱离树，TransformToVisual 会给出失真的坐标。
        // 页签导航在 OnNavigatedFrom 保存；外层卸载沿用最后一次 ViewChanged 快照。
        if (_zoom is not null)
        {
            _position.IsZoomedIn = _zoom.IsZoomedInViewActive;
            _zoom.ViewChangeStarted -= ZoomStarted;
            _zoom.ViewChangeCompleted -= ZoomCompleted;
        }
        _page.RemoveHandler(UIElement.PointerPressedEvent, _pointerIntent);
        _page.RemoveHandler(UIElement.PointerWheelChangedEvent, _pointerIntent);
        _page.RemoveHandler(UIElement.KeyDownEvent, _keyIntent);
        foreach (var view in _views) view.Deactivate();
    }

    private void ZoomStarted(object sender, SemanticZoomViewChangedEventArgs args)
    {
        if (!_initialZoom) CancelRestore();
    }

    private void ZoomCompleted(object sender, SemanticZoomViewChangedEventArgs args) => _initialZoom = false;

    private static string? ItemKey(object item, BrowsePositionKind kind) => item switch
    {
        Music music => kind switch
        {
            BrowsePositionKind.Album => music.Album,
            BrowsePositionKind.Artist => music.Author,
            BrowsePositionKind.Folder => music.LastLevelFolderPath,
            _ => music.Id != 0 ? music.Id.ToString(CultureInfo.InvariantCulture) : music.Path
        },
        Microsoft.UI.Xaml.Data.ICollectionViewGroup { Group: MusicGroup group } => group.Key,
        _ => null
    };

    private sealed class Viewport(ListViewBase list, BrowseViewportPosition position,
        Func<string?> context, BrowsePositionKind kind)
    {
        private ScrollViewer? _scroller;
        private CancellationTokenSource? _request;
        private bool _active;
        private bool _pending;
        private bool _running;
        private bool _interrupted;
        private int _revision;
        private int _activation;
        private int _keyIndex = -1;
        private int _keyRevision = -1;
        private string? _key;

        public void Activate()
        {
            if (_active) return;
            _active = true;
            _activation++;
            _interrupted = false;
            ActiveViews.Remove(list);
            ActiveViews.Add(list, this);
            list.Loaded += ListLoaded;
            list.Items.VectorChanged += ItemsChanged;
            list.SizeChanged += SizeChanged;
            RequestRestore();
        }

        public void Deactivate()
        {
            _active = false;
            _activation++;
            ActiveViews.Remove(list);
            CancelRestore();
            list.Loaded -= ListLoaded;
            list.Items.VectorChanged -= ItemsChanged;
            list.SizeChanged -= SizeChanged;
            if (_scroller is not null) _scroller.ViewChanged -= ViewChanged;
            _scroller = null;
        }

        public void InterruptRestore()
        {
            _interrupted = true;
            CancelRestore();
        }

        private void CancelRestore()
        {
            _pending = false;
            _request?.Cancel();
        }

        public void Capture()
        {
            if (!_active || _pending || _running || list.Items.Count == 0 || context() is not { } identity
                || _scroller is not { ViewportHeight: > 0, ViewportWidth: > 0 } scroll) return;
            int index = list.ItemsPanelRoot switch
            {
                ItemsStackPanel panel => panel.FirstVisibleIndex,
                ItemsWrapGrid panel => panel.FirstVisibleIndex,
                _ => 0
            };
            if (index < 0 || index >= list.Items.Count || list.ContainerFromIndex(index) is not FrameworkElement container) return;
            var point = container.TransformToVisual(scroll).TransformPoint(new Point());
            if (_keyIndex != index || _keyRevision != _revision)
            {
                _keyIndex = index;
                _keyRevision = _revision;
                _key = ItemKey(list.Items[index], kind);
            }
            position.Bookmark = new(identity, _key, index,
                point.X, point.Y, scroll.HorizontalOffset, scroll.VerticalOffset);
        }

        private void ListLoaded(object sender, RoutedEventArgs args) => RequestRestore();

        private void ItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs args)
        {
            _revision++;
            RequestRestore();
        }

        private void SizeChanged(object sender, SizeChangedEventArgs args)
        {
            AttachScroller();
            if (_pending) RequestRestore();
            else Capture();
        }

        private void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            Capture();
        }

        private void AttachScroller()
        {
            if (_scroller is not null || FindScrollViewer(list) is not { } scroll) return;
            _scroller = scroll;
            scroll.ViewChanged += ViewChanged;
        }

        private void RequestRestore()
        {
            if (!_active) return;
            var identity = context();
            if (position.Bookmark is not { } saved || saved.Context != identity)
            {
                _pending = false;
                AttachScroller();
                if (position.Bookmark is { } old && identity is not null && old.Context != identity)
                {
                    _request?.Cancel();
                    position.Bookmark = null;
                    _scroller?.ChangeView(0, 0, null, true);
                }
                Capture();
                return;
            }
            if (_interrupted) return;
            _pending = true;
            if (!_running) _ = RestoreAsync();
        }

        private async Task RestoreAsync()
        {
            _running = true;
            using var request = new CancellationTokenSource();
            _request = request;
            int revision = _revision;
            int activation = _activation;
            bool completed = false;
            try
            {
                // x:Bind、详情绑定和 Frame 导航先完成本轮发布，再读取真实 Items 与布局。
                await Task.Yield();
                if (position.Bookmark is not { } saved || saved.Context != context()) return;
                int target = -1;
                int stable = 0;
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (!_active || !list.IsLoaded || saved.Context != context()) return;
                    list.UpdateLayout();
                    AttachScroller();
                    if (list.Items.Count == 0 || _scroller is not { ViewportHeight: > 0, ViewportWidth: > 0 }) return;
                    if (_revision != revision)
                    {
                        revision = _revision;
                        target = -1;
                        stable = 0;
                    }
                    if (target < 0)
                    {
                        target = Math.Clamp(saved.Index, 0, list.Items.Count - 1);
                        if (saved.AnchorKey is { } key && ItemKey(list.Items[target], kind) != key)
                        {
                            for (int i = 0; i < list.Items.Count; i++)
                                if (ItemKey(list.Items[i], kind) == key) { target = i; break; }
                        }
                        list.ScrollIntoView(list.Items[target], ScrollIntoViewAlignment.Leading);
                        await Task.Delay(16, request.Token);
                        continue;
                    }
                    if (target >= list.Items.Count)
                    {
                        target = -1;
                        continue;
                    }
                    var scroll = _scroller;
                    if (list.ContainerFromIndex(target) is FrameworkElement container)
                    {
                        var point = container.TransformToVisual(scroll).TransformPoint(new Point());
                        double x = Math.Clamp(scroll.HorizontalOffset + point.X - saved.X, 0, scroll.ScrollableWidth);
                        double y = Math.Clamp(scroll.VerticalOffset + point.Y - saved.Y, 0, scroll.ScrollableHeight);
                        if (Math.Abs(x - scroll.HorizontalOffset) < .5 && Math.Abs(y - scroll.VerticalOffset) < .5)
                        {
                            if (++stable >= 2)
                            {
                                completed = true;
                                return;
                            }
                        }
                        else
                        {
                            stable = 0;
                            scroll.ChangeView(x, y, null, true);
                        }
                    }
                    else
                    {
                        // 只为实现容器提供初值；最终位置始终以条目实际坐标校正。
                        scroll.ChangeView(Math.Clamp(saved.HorizontalOffset, 0, scroll.ScrollableWidth),
                            Math.Clamp(saved.VerticalOffset, 0, scroll.ScrollableHeight), null, true);
                        list.ScrollIntoView(list.Items[target], ScrollIntoViewAlignment.Leading);
                    }
                    await Task.Delay(16, request.Token);
                }
                completed = true; // 缺失条目/夹限场景保留最后一个有效浏览位置，不持续抢滚动。
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (_active) App.GetLogger<BrowsePositionKeeper>().LogWarning(exception, "恢复浏览位置失败");
            }
            finally
            {
                if (ReferenceEquals(_request, request)) _request = null;
                _running = false;
                if (completed)
                {
                    _pending = false;
                    Capture();
                }
                else if (_active && _pending && (revision != _revision || activation != _activation)) RequestRestore();
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
