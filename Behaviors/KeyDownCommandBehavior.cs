using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Xaml.Interactivity;
using System.Windows.Input;
using Windows.System;

namespace WinUIMusicPlayer.Behaviors;

public sealed class KeyDownCommandBehavior : Behavior<FrameworkElement>
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(KeyDownCommandBehavior), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(KeyDownCommandBehavior), new PropertyMetadata(null));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public VirtualKey Key { get; set; }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.KeyDown += OnKeyDown;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.KeyDown -= OnKeyDown;
        base.OnDetaching();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!args.Handled) args.Handled = TryExecute(args.Key);
    }

    internal bool TryExecute(VirtualKey key)
    {
        if (key != Key) return false;
        var command = Command;
        var parameter = CommandParameter;
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }
}
