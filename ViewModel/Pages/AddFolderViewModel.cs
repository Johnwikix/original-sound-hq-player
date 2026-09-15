using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel;

public partial class AddFolderViewModel : ObservableObject
{
    public ObservableCollection<Folder> FolderList { get; } = [];
    public Visibility LoadingVisibility { get; private set => SetProperty(ref field, value); } = Visibility.Collapsed;
    public Visibility EmptyVisibility { get; private set => SetProperty(ref field, value); } = Visibility.Collapsed;
    public Visibility ListVisibility { get; private set => SetProperty(ref field, value); } = Visibility.Collapsed;
    public bool IsScanning => LibraryOperationGate.IsBusy;
    private bool NotScanning => !IsScanning;

    private readonly AppViewModel _appViewModel;
    private readonly MusicDatabaseService _database;
    private readonly ILogger<AddFolderViewModel> _logger;
    private readonly Task _initialLoad;
    private bool _needsReconcile;
    private int _folderLoadVersion;

    public AddFolderViewModel(MusicDatabaseService database, AppViewModel appViewModel, ILogger<AddFolderViewModel> logger)
    {
        _database = database;
        _appViewModel = appViewModel;
        _logger = logger;
        LibraryOperationGate.Changed += OnOperationChanged; // Both services are application singletons.
        _initialLoad = LoadFoldersAsync();
    }

    private void OnOperationChanged()
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null) return;
        if (dispatcher.HasThreadAccess) NotifyCommands();
        else dispatcher.TryEnqueue(NotifyCommands);
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsScanning));
        AddFolderCommand.NotifyCanExecuteChanged();
        FolderCommands.NotifyCanExecuteChanged();
        if (IsScanning) _folderLoadVersion++;
        else _ = LoadFoldersAsync(); // Includes startup and watcher scans, which do not call this VM.
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            int version = ++_folderLoadVersion;
            var folders = await _database.GetFoldersWithSongCountsAsync();
            if (version != _folderLoadVersion) return;
            FolderList.Clear();
            foreach (var folder in folders)
            {
                FolderList.Add(folder);
            }
            SetVisualState(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "加载文件夹失败"); }
    }

    private void SetVisualState(bool loading)
    {
        LoadingVisibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyVisibility = !loading && FolderList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListVisibility = !loading && FolderList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task OpenFolderAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        await Launcher.LaunchFolderAsync(folder, new FolderLauncherOptions
        {
            DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
        });
    }

    [RelayCommand(CanExecute = nameof(NotScanning))]
    public Task AddFolderAsync() => AddFolderWithLoadingAsync();

    public Task AddFolderWithLoadingAsync() => RunOperationAsync(async () =>
    {
        var picker = new FolderPicker(App.MainWindow.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null)
            await AddFolderMusicAsync(await StorageFolder.GetFolderFromPathAsync(result.Path));
    });

    public Task DropFoldersAsync(IReadOnlyList<IStorageItem> folders) => RunOperationAsync(async () =>
    {
        foreach (var item in folders)
        {
            // 视图已按 IsOfType(Folder) 筛选；WinRT 接口包装对象不保证能通过 CLR 类型判断。
            // 按路径解析，与选择器入口一致，避免发布后静默跳过文件夹。
            await AddFolderMusicAsync(await StorageFolder.GetFolderFromPathAsync(item.Path));
        }
    });

    public Task RescanFolderWithLoadingAsync(int folderId) => RunOperationAsync(async () =>
    {
        // Existing songs remain visible; new songs use the same committed-batch callback as adding.
        _needsReconcile = true;
        await Task.Run(() => _database.RescanFolder(folderId, ApplyBatchAsync));
    });

    public Task RemoveFolderWithLoadingAsync(int folderId) => RunOperationAsync(async () =>
    {
        var xamlRoot = App.Services.GetRequiredService<MainPage>().XamlRoot;
        if (xamlRoot is null || !await DialogHelper.ShowConfirmAsync(xamlRoot, "RemoveFolderTitle")) return;
        _needsReconcile = true;
        SetVisualState(true);
        await Task.Run(() => _database.RemoveFolder(folderId));
    });

    private async Task RunOperationAsync(Func<Task> operation)
    {
        using var lease = LibraryOperationGate.TryEnter();
        if (lease is null) return;
        _needsReconcile = false;
        try
        {
            await _initialLoad;
            await operation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件夹操作失败，已提交的扫描批次保留，可重新扫描继续");
        }
        finally
        {
            try
            {
                // Also reconcile partially committed scans after an enumeration/metadata/database failure.
                if (_needsReconcile)
                {
                    await _appViewModel.RefreshSongsSourceAsync();
                    await LoadFoldersAsync();
                }
            }
            finally { SetVisualState(false); }
        }
    }

    private Task AddFolderMusicAsync(StorageFolder folder)
    {
        _needsReconcile = true;
        return Task.Run(() =>
        _database.CheckFolderBeforeAdd(folder,
            onFolderInserted: added => App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                FolderList.Add(added);
                SetVisualState(false);
            }),
            onBatchInserted: ApplyBatchAsync));
    }

    private Task ApplyBatchAsync(IReadOnlyList<Music> batch) =>
        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            _appViewModel.AppendSongsBatch(batch);
            foreach (var folder in FolderList)
            {
                int count = 0;
                foreach (var music in batch)
                    if (LibraryPath.IsWithin(music.Path, folder.Path)) count++;
                folder.SongCount += count;
            }
        });
}
