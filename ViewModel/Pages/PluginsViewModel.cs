using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.System;
using WinUIMusicPlayer.Services.Plugins;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Pages;

public sealed partial class PluginsViewModel(PluginManager manager) : ObservableObject
{
    public ObservableCollection<PluginItem> Items => manager.Items;
    public string DirectoryPath => manager.Root;
    [ObservableProperty] private string message = ToolUtils.GetString("PluginsInstructions");

    [RelayCommand]
    private async Task ToggleAsync(PluginItem item)
    {
        try { await manager.SetEnabledAsync(item, !item.Enabled); }
        catch (OperationCanceledException) { }
        catch { Message = ToolUtils.GetString("PluginSaveFailed"); }
    }
    [RelayCommand]
    private async Task RetryAsync(PluginItem item)
    {
        try { await manager.SetEnabledAsync(item, true); }
        catch (OperationCanceledException) { }
        catch { Message = ToolUtils.GetString("PluginSaveFailed"); }
    }
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        try
        {
            System.IO.Directory.CreateDirectory(manager.Root);
            await Launcher.LaunchFolderPathAsync(manager.Root);
        }
        catch { Message = ToolUtils.GetString("PluginSaveFailed"); }
    }
}
