using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinUIMusicPlayer.Helper;

internal static class ContentDialogLayout
{
    private static readonly Thickness VerticalMargin = new(0, 12, 0, 12);
    private static readonly CornerRadius CloseButtonRadius = new(4);

    internal static IAsyncOperation<ContentDialogResult> ShowWithLayoutAsync(this ContentDialog dialog)
    {
        // Detached dialogs realize their template when they join the popup's visual tree.
        dialog.Loaded -= OnLoaded;
        dialog.Loaded += OnLoaded;
        dialog.Opened -= OnOpened;
        dialog.Opened += OnOpened;
        dialog.Closed -= OnClosed;
        dialog.Closed += OnClosed;
        return dialog.ShowAsync();
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) =>
        ApplyLayout((ContentDialog)sender);

    private static void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        ApplyLayout(sender);

    private static void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        sender.Loaded -= OnLoaded;
        sender.Opened -= OnOpened;
        sender.Closed -= OnClosed;
    }

    private static void ApplyLayout(ContentDialog dialog)
    {
        // For in-tree dialogs WinUI can transplant LayoutRoot into a separate popup.
        if (dialog.XamlRoot is { } xamlRoot)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is { } child &&
                    (child == dialog || child is FrameworkElement { Name: "LayoutRoot" }) &&
                    TryApplyLayout(child)) return;
            }
        }

        TryApplyLayout(dialog);
    }

    private static bool TryApplyLayout(DependencyObject root)
    {
        // In WinUI's default template the surface and smoke layer are separate.
        if (FindPart(root, "BackgroundElement") is not Border surface) return false;
        surface.Margin = VerticalMargin;
        if (FindPart(surface, "CloseButton") is Button closeButton)
            closeButton.CornerRadius = CloseButtonRadius;
        return true;
    }

    private static FrameworkElement? FindPart(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            if (FindPart(VisualTreeHelper.GetChild(root, index), name) is { } part)
                return part;
        }
        return null;
    }
}
