using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace WinUIMusicPlayer.Behaviors;

/// <summary>视图拥有的手动定位操作。等待虚拟化容器就绪后居中，卸载或新请求会取消旧操作。</summary>
public sealed class ListViewCentering
{
    private readonly ListViewBase _list;
    private CancellationTokenSource? _request;

    public ListViewCentering(ListViewBase list) => _list = list;

    public void Cancel() => _request?.Cancel();

    public Task<bool> LocateAsync(object? item) => LocateCoreAsync(item, null);

    public Task<bool> LocateIndexAsync(int index)
        => LocateCoreAsync(index >= 0 && index < _list.Items.Count ? _list.Items[index] : null, index);

    private async Task<bool> LocateCoreAsync(object? item, int? index)
    {
        BrowsePositionKeeper.InterruptRestore(_list);
        Cancel();
        if (item is null || !_list.IsLoaded || !_list.Items.Contains(item)) return false;
        using var request = new CancellationTokenSource();
        _request = request;
        RoutedEventHandler onUnloaded = (_, _) => request.Cancel();
        VectorChangedEventHandler<object> onItemsChanged = (_, _) => request.Cancel();
        _list.Unloaded += onUnloaded;
        _list.Items.VectorChanged += onItemsChanged;
        try
        {
            _list.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
            for (int attempt = 0; attempt < 24; attempt++)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!_list.IsLoaded || !_list.Items.Contains(item)) return false;
                _list.UpdateLayout();
                if (index is { } targetIndex && (targetIndex >= _list.Items.Count
                    || !ReferenceEquals(_list.Items[targetIndex], item))) return false;
                var container = (index is { } position ? _list.ContainerFromIndex(position) : _list.ContainerFromItem(item)) as FrameworkElement;
                if (FindScrollViewer(_list) is { ViewportHeight: > 0 } scroller)
                {
                    if (container is { ActualHeight: > 0 })
                    {
                        double top = container.TransformToVisual(scroller).TransformPoint(new Point()).Y;
                        double centerTop = (scroller.ViewportHeight - container.ActualHeight) / 2;
                        double offset = Math.Clamp(scroller.VerticalOffset + top - centerTop, 0, scroller.ScrollableHeight);
                        if (Math.Abs(offset - scroller.VerticalOffset) < .5) return true;
                        scroller.ChangeView(null, offset, null, true);
                    }
                    else if (index is { } unrealized)
                    {
                        // ScrollIntoView 只能按对象定位；重复队列对象会指向首次出现的位置。
                        // 先用可见范围估算步长生成指定索引的容器，再用实际行坐标精确居中。
                        var panel = _list.ItemsPanelRoot as ItemsStackPanel;
                        double pitch = scroller.ExtentHeight / _list.Items.Count;
                        double offset = panel is { FirstVisibleIndex: >= 0 }
                            ? scroller.VerticalOffset + (unrealized - panel.FirstVisibleIndex) * pitch
                            : unrealized * pitch;
                        scroller.ChangeView(null, Math.Clamp(offset, 0, scroller.ScrollableHeight), null, true);
                    }
                }
                await Task.Delay(16, request.Token);
            }
            return false;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (Exception) when (request.IsCancellationRequested || !_list.IsLoaded) { return false; }
        finally
        {
            _list.Unloaded -= onUnloaded;
            _list.Items.VectorChanged -= onItemsChanged;
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
