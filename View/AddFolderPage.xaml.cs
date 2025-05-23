using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFolderPage : Page
    {
        private AddFolderService addFolderService = new AddFolderService();
        private NotificationService notificationService;
        public AddFolderPage()
        {
            this.InitializeComponent();
            notificationService = new NotificationService();
            var mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.FoldersLoaded += MainWindow_FoldersLoaded;
                mainWindow.LoadFoldersAsync();
            }
        }

        private void MainWindow_FoldersLoaded(object sender, IEnumerable<Folder> folderList)
        {
            try
            {
                FolderListView.ItemsSource = folderList;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新文件夹列表时出错: {ex.Message}");
            }
        }

        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await MusicDatabaseService.GetFolders();
                FolderListView.ItemsSource = folderList;
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
            }
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        { // This is the correct method signature
            var button = sender as Button;
            if (button != null && button.Tag is int folderId)
            {
                var folderToOpen = await MusicDatabaseService.GetFolder(folderId);
                if (folderToOpen != null)
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderToOpen.Path);
                    var options = new FolderLauncherOptions
                    {
                        DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
                    };
                    await Launcher.LaunchFolderAsync(folder, options);
                }
            }
        }


        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {

            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");
            //var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, AppData.m_hWnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            await AddFolderMusic(folder);
            LoadingGrid.Visibility = Visibility.Collapsed;
            AddFolderGrid.Visibility = Visibility.Visible;
        }

        private async Task AddFolderMusic(StorageFolder folder) {
            if (folder != null)
            {
                await Task.Run(() => MusicDatabaseService.CheckFolderBeforeAdd(folder));
                await LoadFoldersAsync();
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
                {
                    mainWindow.UpdateMusicList();
                }
            }
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
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            var button = sender as Button;
            if (button != null && button.Tag is int folderId)
            {
                await MusicDatabaseService.RemoveFolder(folderId);
                await LoadFoldersAsync();
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
                {
                    mainWindow.UpdateMusicList();
                    //await mainWindow.LoadMusicList();
                    //await mainWindow.LoadFavourMusicList();
                }
            }
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
            // 恢复原来的背景色

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();

                    // 筛选出文件夹
                    var folders = items.Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder));

                    if (folders.Any())
                    {
                        var firstFolder = folders.First();
                        string folderPath = firstFolder.Path;
                        Debug.WriteLine($"路径: {folderPath}");
                        // 在这里可以进一步处理文件夹路径
                        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                        LoadingGrid.Visibility = Visibility.Visible;
                        AddFolderGrid.Visibility = Visibility.Collapsed;
                        await AddFolderMusic(folder);
                        LoadingGrid.Visibility = Visibility.Collapsed;
                        AddFolderGrid.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        Debug.WriteLine("请拖拽文件夹，不是文件");
                        notificationService.SendNotification(ToolUtils.GetString("Warning"), ToolUtils.GetString("PleaseDragFolder"));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"错误: {ex.Message}");
                    notificationService.SendNotification(ToolUtils.GetString("Error"), ex.Message);
                }
            }
        }
    }
}
