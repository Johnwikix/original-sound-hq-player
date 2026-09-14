using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.ApplicationModel.DataTransfer;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFolderPage : Page
    {
        private static ILogger<AddFolderPage> _logger = App.GetLogger<AddFolderPage>();
        public AddFolderViewModel ViewModel { get; }

        public AddFolderPage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<AddFolderViewModel>();
            DataContext = this;
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
        }

        // 拖放属视图层交互：DragOver/DragLeave 只驱动 DropOverlay 瞬时反馈，
        // Drop 提取文件夹后转发 ViewModel.DropFoldersAsync（含 Loading 状态与入库逻辑）。

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
                DropOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
                DropOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void Grid_DragLeave(object sender, DragEventArgs e)
        {
            var position = e.GetPosition(this);
            if (position.X < 0 || position.Y < 0 ||
                position.X > ActualWidth || position.Y > ActualHeight)
            {
                DropOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    // 筛选出文件夹
                    var folders = items.AsValueEnumerable().Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder)).ToList();

                    if (folders.Count > 0)
                    {
                        await ViewModel.DropFoldersAsync(folders);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Grid_Drop 拖放文件夹失败: {ex.Message}");
                }
            }
        }
    }
}
