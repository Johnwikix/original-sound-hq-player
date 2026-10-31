using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace WinUIMusicPlayer.Helper;

public static class ListViewInteraction
{
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
