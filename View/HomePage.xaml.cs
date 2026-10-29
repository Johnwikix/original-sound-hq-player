using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View;

/// <summary>显示首页并转发拖放交互，状态和播放均由 ViewModel 管理。</summary>
public sealed partial class HomePage : Page
{
    /// <summary>获取稳定的首页 ViewModel。</summary>
    public HomeViewModel ViewModel { get; }
    /// <summary>初始化页面，不阻塞主窗口启动。</summary>
    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }
    /// <summary>转换非空集合的可见性。</summary>
    public Visibility ShowCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>转换非空消息状态。</summary>
    public bool HasText(string text) => !string.IsNullOrWhiteSpace(text);
    /// <summary>将通用轮播选项适配为首页主题，主视图保持同一个实例。</summary>
    public HomeTheme? SelectedTheme(int index, int count) => index >= 0 && index < count ? Featured.SelectedItem as HomeTheme : null;
    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        Bindings.Initialize();
        Bindings.Update();
        ViewModel.Activate(DispatcherQueue);
    }
    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.Deactivate();
        DropOverlay.Visibility = Visibility.Collapsed;
        Bindings.StopTracking();
    }
    private void Empty_DragOver(object sender, DragEventArgs args)
    {
        bool accept = !ViewModel.IsScanning && args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = accept ? DataPackageOperation.Link : DataPackageOperation.None;
        DropOverlay.Visibility = accept ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Empty_DragLeave(object sender, DragEventArgs args) => DropOverlay.Visibility = Visibility.Collapsed;
    private async void Empty_Drop(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (ViewModel.IsScanning || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        try
        {
            var folders = new List<IStorageItem>();
            foreach (var item in await args.DataView.GetStorageItemsAsync())
                if (item.IsOfType(StorageItemTypes.Folder)) folders.Add(item);
            if (IsLoaded && folders.Count > 0) await ViewModel.DropFoldersAsync(folders);
        }
        catch (Exception ex) { App.GetLogger<HomePage>().LogError(ex, "首页拖入文件夹失败"); }
        finally { deferral.Complete(); }
    }
}
