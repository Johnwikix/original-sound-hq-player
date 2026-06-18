using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
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
        private MusicDatabaseService _musicDatabaseService { get; }
        public AddFolderPage()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<AddFolderViewModel>();
            DataContext = this;
            _musicDatabaseService = App.Services.GetRequiredService<MusicDatabaseService>();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button is not null && button.Tag is string folderPath)
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
            if (button is not null && button.Tag is int folderId)
            {
                await Task.Run(() => _musicDatabaseService.RescanFolder(folderId));
            }
            LoadingGrid.Visibility = Visibility.Collapsed;
            AddFolderGrid.Visibility = Visibility.Visible;
        }

        private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (await DialogHelper.ShowConfirmAsync(this.XamlRoot, "RemoveFolderTitle"))
            {
                var button = sender as Button;
                if (button is not null && button.Tag is int folderId)
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
                    var folders = items.AsValueEnumerable().Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder));

                    if (folders.Any())
                    {
                        LoadingGrid.Visibility = Visibility.Visible;
                        AddFolderGrid.Visibility = Visibility.Collapsed;
                        ViewModel.Grid_Drop(folders.ToList());
                        LoadingGrid.Visibility = Visibility.Collapsed;
                        AddFolderGrid.Visibility = Visibility.Visible;
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
