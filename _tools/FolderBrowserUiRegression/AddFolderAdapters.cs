using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using Windows.Storage;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.ViewModel
{
    // 保留生产 AddFolderPage/XAML 与真实 DropDownButton，隔离文件选择器和网络服务。
    public sealed class AddFolderViewModel
    {
        public ObservableCollection<Folder> FolderList { get; } = [new() { Name = "Music", Path = @"D:\Music", SongCount = 2 }];
        public Visibility LoadingVisibility => Visibility.Collapsed;
        public Visibility EmptyVisibility => Visibility.Collapsed;
        public Visibility ListVisibility => Visibility.Visible;
        public bool IsScanning => false;
        public IRelayCommand AddFolderCommand { get; } = new RelayCommand(() => { });
        public void Activate() { }
        public void Deactivate() { }
        public Task DropFoldersAsync(IReadOnlyList<IStorageItem> folders) => Task.CompletedTask;
        public Task OpenFolderAsync(string path) => Task.CompletedTask;
        public Task RescanFolderWithLoadingAsync(int id) => Task.CompletedTask;
        public Task RemoveFolderWithLoadingAsync(int id) => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer.Services
{
    public static class LibraryOperationGate
    {
        public static bool IsBusy => false;
    }
}
namespace WinUIMusicPlayer.View.SubView
{
    public sealed partial class WebDavSourcesControl : UserControl
    {
        public Task ShowAddAsync() => Task.CompletedTask;
    }
}
