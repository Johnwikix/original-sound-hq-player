using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;

namespace WinUIMusicPlayer.Behaviors;

public sealed class ScrollSelectedItemIntoViewBehavior : Behavior<ListViewBase>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SelectionChanged += OnSelectionChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SelectionChanged -= OnSelectionChanged;
        base.OnDetaching();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (AssociatedObject.SelectedItem is { } selected)
            AssociatedObject.ScrollIntoView(selected);
    }
}
