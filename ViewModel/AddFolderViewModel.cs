using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

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

        public AddFolderViewModel()
        {
            // Initialize with some default folders if needed
            // FolderList.Add(new Folder { Name = "Default Folder", Path = "C:\\DefaultPath" });
            _ = LoadFoldersAsync();
        }

        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await MusicDatabaseService.GetFolders();
                FolderList.Clear();
                foreach (var folder in folderList)
                {
                    FolderList.Add(folder);
                }
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite 错误: {ex.Message}");
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
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, AppData.m_hWnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            await AddFolderMusic(folder);
        }
        private async Task AddFolderMusic(StorageFolder folder)
        {
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

        public async void RemoveFolderButton_Click(int folderId)
        {
            await MusicDatabaseService.RemoveFolder(folderId);
            await LoadFoldersAsync();
            var mainWindow = (App.MainWindow as MainWindow);
            if (mainWindow != null)
            {
                mainWindow.UpdateMusicList();
            }
        }

        public async void Grid_Drop(IEnumerable<IStorageItem> folders)
        {
            foreach (var item in folders)
            {
                string folderPath = item.Path;
                Debug.WriteLine($"路径: {folderPath}");
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                await AddFolderMusic(folder);
            }
        }
    }
}
