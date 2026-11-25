using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

public static class ListViewInteraction
{
    public static bool HasVisibleMusicRow(ListView list) =>
        list.IsLoaded &&
        list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } panel &&
        list.ContainerFromIndex(panel.FirstVisibleIndex) is ListViewItem { IsLoaded: true, ActualHeight: > 0, Content: Music };

    public static bool IsAction(DependencyObject? source, ListViewBase list)
    {
        for (var element = source; element is not null && !ReferenceEquals(element, list); element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return true;
        return false;
    }
    public static int IndexFromSource(DependencyObject? source, ListViewBase list)
    {
        for (var element = source; element is not null && !ReferenceEquals(element, list); element = VisualTreeHelper.GetParent(element))
            if (element is ListViewItem item) return list.IndexFromContainer(item);
        return -1;
    }
}
