using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFolderPage : Page
    {
        private NotificationService notificationService;
        public AddFolderViewModel ViewModel { get; }
        public AddFolderPage(NotificationService notificationService, AddFolderViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            DataContext = this;
            this.notificationService = notificationService;
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is string folderPath)
            {
                ViewModel.OpenFolderButton_Click(folderPath);
            }
        }


        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            await ViewModel.AddFolderButton_Click();
            LoadingGrid.Visibility = Visibility.Collapsed;
            AddFolderGrid.Visibility = Visibility.Visible;
        }


        private async void RescanFolderButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            var button = sender as Button;
            if (button != null && button.Tag is int folderId)
            {
                await Task.Run(() => MusicDatabaseService.RescanFolder(folderId));
            }
            LoadingGrid.Visibility = Visibility.Collapsed;
            AddFolderGrid.Visibility = Visibility.Visible;
        }

        private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            ContentDialog contentDialog = new ContentDialog
            {
                Title = ToolUtils.GetString("RemoveFolderTitle"),
                PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                CloseButtonText = ToolUtils.GetString("CloseButton"),
                XamlRoot = this.XamlRoot
            };
            contentDialog.RequestedTheme = AppSettings.elementTheme;
            ContentDialogResult result = await contentDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var button = sender as Button;
                if (button != null && button.Tag is int folderId)
                {
                    RemoveFolder(folderId);
                }
            }
        }

        private async void RemoveFolder(int folderId)
        {
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            await ViewModel.RemoveFolderButton_Click(folderId);
            LoadingGrid.Visibility = Visibility.Collapsed;
            AddFolderGrid.Visibility = Visibility.Visible;
        }

        private void Grid_DragOver(object sender, DragEventArgs e)
        {
            // 检查拖拽的数据是否包含文件
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Link;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void Grid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    // 筛选出文件夹
                    IEnumerable<IStorageItem> folders = items.Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder));

                    if (folders.Any())
                    {
                        LoadingGrid.Visibility = Visibility.Visible;
                        AddFolderGrid.Visibility = Visibility.Collapsed;
                        ViewModel.Grid_Drop(folders);
                        LoadingGrid.Visibility = Visibility.Collapsed;
                        AddFolderGrid.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        notificationService.SendNotification(ToolUtils.GetString("Warning"), ToolUtils.GetString("PleaseDragFolder"));
                    }
                }
                catch (Exception ex)
                {
                    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
        }
    }
}
