using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage.Pickers;
using SQLite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using ZLinq;
namespace WinUIMusicPlayer.ViewModel
{
    public partial class AddFolderViewModel : ObservableObject
    {
        private ObservableCollection<Folder> _folderList = [];
        public ObservableCollection<Folder> FolderList
        {
            get => _folderList;
            set => SetProperty(ref _folderList, value);
        }
        private AppViewModel AppViewModel { get; set; }
        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<AddFolderViewModel> _logger { get; }

        public AddFolderViewModel(MusicDatabaseService musicDatabaseService, AppViewModel appViewModel, ILogger<AddFolderViewModel> logger)
        {
            _musicDatabaseService = musicDatabaseService;
            AppViewModel = appViewModel;
            _logger = logger;
            _ = LoadFoldersAsync();
        }

        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await _musicDatabaseService.GetFolders();
                FolderList.Clear();
                foreach (var folder in folderList)
                {
                    folder.SongCount = AppViewModel.SongsSource.AsValueEnumerable().Where(m => m.Path.StartsWith(folder.Path)).Count();
                    FolderList.Add(folder);
                }
            }
            catch (SQLiteException ex)
            {
                _logger.LogError(ex, $"SQLite 错误: {ex.Message}");
            }
        }

        public async void OpenFolderButton_Click(string folderPath)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var options = new FolderLauncherOptions
            {
                DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
            };
            await Launcher.LaunchFolderAsync(folder, options);
        }

        public async Task AddFolderButton_Click()
        {
            try
            {
                var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
                PickFolderResult result = await folderPicker.PickSingleFolderAsync();
                if (result is null) return;
                await AddFolderMusic(await StorageFolder.GetFolderFromPathAsync(result.Path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"添加文件夹失败: {ex.Message}");
            }
        }
        private async Task AddFolderMusic(StorageFolder folder)
        {
            if (folder is not null)
            {
                await Task.Run(() => _musicDatabaseService.CheckFolderBeforeAdd(folder));
                AppViewModel.SongsSource.Clear();
                AppViewModel.SongsSource.AddRange(await _musicDatabaseService.GetMusicListAsync());
                AppViewModel.NotifySongsSourceChanged();
                await LoadFoldersAsync();
            }
        }

        public async Task RemoveFolderButton_Click(int folderId)
        {
            await Task.Run(() => _musicDatabaseService.RemoveFolder(folderId));
            AppViewModel.SongsSource.Clear();
            AppViewModel.SongsSource.AddRange(await _musicDatabaseService.GetMusicListAsync());
            AppViewModel.NotifySongsSourceChanged();
            await LoadFoldersAsync();
        }

        public async void Grid_Drop(IEnumerable<IStorageItem> folders)
        {
            foreach (var item in folders)
            {
                string folderPath = item.Path;
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                await AddFolderMusic(folder);
            }
        }
    }
}
