using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>目录浏览视图；交互转发给会话 ViewModel，控件不拥有目录状态。</summary>
public sealed partial class FolderBrowsePage : Page
{
    private bool _ownsActivation;
    public FolderViewModel ViewModel { get; }
    public FolderBrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<FolderViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Disabled;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    public bool HasText(string text) => !string.IsNullOrEmpty(text);
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_ownsActivation) return;
        Bindings.Initialize();
        Bindings.Update();
        _ownsActivation = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        await ViewModel.ActivateAsync();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        DeactivateView();
        Bindings.StopTracking();
    }
    private void DeactivateView()
    {
        if (!_ownsActivation) return;
        _ownsActivation = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Deactivate();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        DeactivateView();
        base.OnNavigatedFrom(args);
    }
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FolderViewModel.SelectedNode) && ViewModel.SelectedNode is { } selected)
            FolderTree.ScrollIntoView(selected);
    }

    private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ownsActivation) return;
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is FolderBrowserNode node)
            ViewModel.SelectFolderCommand.Execute(node);
    }

    private void TreeRowToggle_Click(object sender, RoutedEventArgs args)
    {
        if (!_ownsActivation || (sender as FrameworkElement)?.DataContext is not FolderBrowserNode node) return;
        if (node.IsExpanded) ViewModel.CollapseFolder(node);
        else ViewModel.ExpandFolder(node);
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_ownsActivation || FolderTree.SelectedItem is not FolderBrowserNode node) return;
        if (args.Key == VirtualKey.Right && node.HasChildren)
        {
            if (node.IsExpanded) FolderTree.SelectedItem = node.Children[0];
            else ViewModel.ExpandFolder(node);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Left)
        {
            if (node.IsExpanded) ViewModel.CollapseFolder(node);
            else if (node.Parent is not null) FolderTree.SelectedItem = node.Parent;
            else return;
            args.Handled = true;
        }
    }
}
