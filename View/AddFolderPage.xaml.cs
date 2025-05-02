using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SQLite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

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
        public AddFolderPage()
        {
            this.InitializeComponent();

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
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            LoadingGrid.Visibility = Visibility.Visible;
            AddFolderGrid.Visibility = Visibility.Collapsed;
            if (folder != null)
            {
                await Task.Run(() => MusicDatabaseService.CheckFolderBeforeAdd(folder));
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
    }
}
