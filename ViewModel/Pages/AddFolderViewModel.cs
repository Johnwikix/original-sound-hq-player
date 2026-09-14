using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using SQLite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using ZLinq;
namespace WinUIMusicPlayer.ViewModel
{
    public partial class AddFolderViewModel : ObservableObject
    {
        public ObservableCollection<Folder> FolderList { get => field; set => SetProperty(ref field, value); } = [];
        // Loading / 空引导 / 列表 三态互斥视图状态（渲染在 AddFolderPage）；
        // MusicBrowsePage 空库占位跨页复用本 VM 的添加/拖入流程，其 Loading 反馈走顶部 ProgressRing。
        public Visibility LoadingVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public Visibility EmptyVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
        public Visibility ListVisibility { get; set => SetProperty(ref field, value); } = Visibility.Collapsed;
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
                    var span = CollectionsMarshal.AsSpan(AppViewModel.SongsSource);
                    int count = 0;
                    string folderPath = folder.Path;
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (span[i].Path.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                            count++;
                    }
                    folder.SongCount = count;
                    FolderList.Add(folder);
                }
                SetVisualStateOnUi(isLoading: false);
            }
            catch (SQLiteException ex)
            {
                _logger.LogError(ex, $"SQLite 错误: {ex.Message}");
                SetVisualStateOnUi(isLoading: false);
            }
        }

        // LoadFoldersAsync 可能在非 UI 线程收尾，视图状态必须回 UI 线程设置
        private void SetVisualStateOnUi(bool isLoading)
        {
            void Apply()
            {
                if (isLoading)
                {
                    LoadingVisibility = Visibility.Visible;
                    EmptyVisibility = Visibility.Collapsed;
                    ListVisibility = Visibility.Collapsed;
                    return;
                }
                LoadingVisibility = Visibility.Collapsed;
                bool hasFolders = FolderList.Count > 0;
                EmptyVisibility = hasFolders ? Visibility.Collapsed : Visibility.Visible;
                ListVisibility = hasFolders ? Visibility.Visible : Visibility.Collapsed;
            }
            var dq = App.MainWindow?.DispatcherQueue;
            if (dq is not null && !dq.HasThreadAccess)
            {
                dq.TryEnqueue(Apply);
            }
            else
            {
                Apply();
            }
        }

        public async Task OpenFolderAsync(string folderPath)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var options = new FolderLauncherOptions
            {
                DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
            };
            await Launcher.LaunchFolderAsync(folder, options);
        }

        [RelayCommand]
        public Task AddFolderAsync() => AddFolderWithLoadingAsync();

        /// <summary>
        /// 打开系统文件夹选择器并入库；AddFolderPage 按钮与 MusicBrowsePage 空库占位共用。
        /// </summary>
        public async Task AddFolderWithLoadingAsync()
        {
            SetVisualStateOnUi(isLoading: true);
            try
            {
                var folderPicker = new FolderPicker(App.MainWindow.AppWindow.Id);
                PickFolderResult result = await folderPicker.PickSingleFolderAsync();
                if (result is null) return;
                await AddFolderMusic(await StorageFolder.GetFolderFromPathAsync(result.Path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"添加文件夹失败: {ex.Message}");
            }
            finally
            {
                SetVisualStateOnUi(isLoading: false);
            }
        }

        public async Task RescanFolderWithLoadingAsync(int folderId)
        {
            SetVisualStateOnUi(isLoading: true);
            try
            {
                await Task.Run(() => _musicDatabaseService.RescanFolder(folderId));
            }
            finally
            {
                SetVisualStateOnUi(isLoading: false);
            }
        }

        public async Task RemoveFolderWithLoadingAsync(int folderId)
        {
            var xamlRoot = App.Services.GetRequiredService<MainPage>().XamlRoot;
            if (xamlRoot is null) return;
            if (!await DialogHelper.ShowConfirmAsync(xamlRoot, "RemoveFolderTitle")) return;
            SetVisualStateOnUi(isLoading: true);
            try
            {
                await Task.Run(() => _musicDatabaseService.RemoveFolder(folderId));
                await AppViewModel.RefreshSongsSourceAsync();
                await LoadFoldersAsync();
            }
            finally
            {
                SetVisualStateOnUi(isLoading: false);
            }
        }

        public async Task DropFoldersAsync(IReadOnlyList<IStorageItem> folders)
        {
            SetVisualStateOnUi(isLoading: true);
            try
            {
                foreach (var item in folders)
                {
                    await AddFolderMusic(await StorageFolder.GetFolderFromPathAsync(item.Path));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"DropFoldersAsync 拖放文件夹失败: {ex.Message}");
            }
            finally
            {
                SetVisualStateOnUi(isLoading: false);
            }
        }

        private async Task AddFolderMusic(StorageFolder folder)
        {
            if (folder is not null)
            {
                await Task.Run(() => _musicDatabaseService.CheckFolderBeforeAdd(folder));
                await AppViewModel.RefreshSongsSourceAsync();
                await LoadFoldersAsync();
            }
        }
    }
}
