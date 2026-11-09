using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using System.Windows.Input;

namespace WinUIMusicPlayer.Behaviors;

public sealed class SelectionChangedCommandBehavior : Behavior<ListViewBase>
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(SelectionChangedCommandBehavior), new PropertyMetadata(null));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

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
        var command = Command;
        var selected = AssociatedObject.SelectedItem;
        if (command?.CanExecute(selected) == true) command.Execute(selected);
    }
}
