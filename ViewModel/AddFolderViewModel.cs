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
using System.Linq;
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

        public AddFolderViewModel()
        {
            _ = LoadFoldersAsync();
        }

        private async Task LoadFoldersAsync()
        {
            try
            {
                var folderList = await MusicDatabaseService.GetFolders();
                FolderList.Clear();
                Debug.WriteLine(AppData.allSongs.AsValueEnumerable().Count());
                foreach (var folder in folderList)
                {
                    folder.SongCount = AppData.allSongs.AsValueEnumerable().Where(m => m.Path.StartsWith(folder.Path)).Count();
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
            //try
            //{
            //    var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
            //    PickFolderResult result = await folderPicker.PickSingleFolderAsync();
            //    if (result is null) return;
            //    await AddFolderMusic(await StorageFolder.GetFolderFromPathAsync(result.Path));
            //}
            //catch (Exception)
            //{
            //}
        }
        private async Task AddFolderMusic(StorageFolder folder)
        {
            if (folder is not null)
            {
                await Task.Run(() => MusicDatabaseService.CheckFolderBeforeAdd(folder));
                AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                await LoadFoldersAsync();
                App.MainWindow?.UpdateMusicList();                
            }
        }

        public async Task RemoveFolderButton_Click(int folderId)
        {
            await Task.Run(() => MusicDatabaseService.RemoveFolder(folderId));
            await LoadFoldersAsync();
            App.MainWindow?.UpdateMusicList();
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
