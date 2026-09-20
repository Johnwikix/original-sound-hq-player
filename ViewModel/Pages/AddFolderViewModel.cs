using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
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
    private readonly FolderAccessService _folderAccess;
    private Task? _initialLoad;
    private Task _operationTask = Task.CompletedTask;
    private bool _active;
    private bool _started;
    private bool _stopped;
    private bool _needsReconcile;
    private int _folderLoadVersion;

    public AddFolderViewModel(MusicDatabaseService database, AppViewModel appViewModel, ILogger<AddFolderViewModel> logger, FolderAccessService folderAccess)
    {
        _database = database;
        _appViewModel = appViewModel;
        _logger = logger;
        _folderAccess = folderAccess;
    }

    public void Start()
    {
        if (_started || _stopped) return;
        _started = true;
        LibraryOperationGate.Changed += OnOperationChanged;
    }

    public void Activate()
    {
        if (_stopped) return;
        Start();
        _active = true;
        _initialLoad = LoadFoldersAsync();
    }

    public void Deactivate() => _active = false;

    public async Task StopAsync()
    {
        _stopped = true;
        _active = false;
        _folderLoadVersion++;
        LibraryOperationGate.Changed -= OnOperationChanged;
        if (_initialLoad is not null) await _initialLoad;
        await _operationTask;
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
        if (_stopped) return;
        OnPropertyChanged(nameof(IsScanning));
        AddFolderCommand.NotifyCanExecuteChanged();
        FolderCommands.NotifyCanExecuteChanged();
        if (IsScanning) _folderLoadVersion++;
        else if (_active) _initialLoad = LoadFoldersAsync();
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
                if (folder.IsExternalImport)
                {
                    // 显示名在展示层注入资源文案（库内不固化本地化文本，避免切换语言后过期）；
                    // 哨兵路径不展示，业务逻辑一律走 IsExternalImport/Id。
                    folder.Name = ToolUtils.GetString("ExternalImportsFolderName");
                    folder.Path = string.Empty;
                }
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
        try
        {
            await _folderAccess.OpenAsync(path);
        }
        catch (Exception ex)
        {
            // FolderCommands 的快捷入口不等待此任务，异常必须在这里观察。
            _logger.LogError(ex, "打开文件夹失败: {Path}", path);
        }
    }

    [RelayCommand(CanExecute = nameof(NotScanning))]
    public Task AddFolderAsync() => AddFolderWithLoadingAsync();

    public Task AddFolderWithLoadingAsync() => RunOperationAsync(async () =>
    {
        var result = await _folderAccess.PickAsync(App.MainWindow.AppWindow.Id, AppData.HWnd);
        if (result is not null)
            await AddFolderMusicAsync(result);
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
        var folder = await _database.GetFolder(folderId);
        if (folder is null) return;
        // 虚拟行移除的是全部外部导入歌曲，确认文案与普通文件夹区分。
        string titleKey = folder.IsExternalImport ? "RemoveExternalImportsTitle" : "RemoveFolderTitle";
        if (xamlRoot is null || !await DialogHelper.ShowConfirmAsync(xamlRoot, titleKey)) return;
        _needsReconcile = true;
        SetVisualState(true);
        await Task.Run(() => _database.RemoveFolder(folderId));
    });

    private Task RunOperationAsync(Func<Task> operation)
    {
        if (_stopped || !_operationTask.IsCompleted) return Task.CompletedTask;
        _operationTask = RunOperationCoreAsync(operation);
        return _operationTask;
    }

    private async Task RunOperationCoreAsync(Func<Task> operation)
    {
        if (_stopped) return;
        using var lease = LibraryOperationGate.TryEnter();
        if (lease is null) return;
        _needsReconcile = false;
        try
        {
            await (_initialLoad ??= LoadFoldersAsync());
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

    /// <summary>在 UI 线程发布导入歌曲，并从数据库同步文件夹行、归属计数与空状态。</summary>
    internal async Task PublishImportedAsync(Music music)
    {
        if (_stopped) return;
        // 在首次 await 前检查并发布，避免并发打开同一文件造成重复歌曲。
        if (_appViewModel.FindById(music.Id) is null)
        {
            _appViewModel.AppendSongsBatch([music]);
            _appViewModel.NotifySongsSourceChanged();
        }
        // 首次导入可能刚创建虚拟行，不能仅对已有 FolderList 递增计数。
        await LoadFoldersAsync();
    }

    /// <summary>扫描批次的发布路径：歌曲索引增量 + 各扫描根计数递增；
    /// 外部导入虚拟行的计数按归属（不落在任何扫描根内的批次条目）同步递增。</summary>
    internal Task ApplyBatchAsync(IReadOnlyList<Music> batch) =>
        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            _appViewModel.AppendSongsBatch(batch);
            Folder? external = null;
            foreach (var folder in FolderList)
            {
                if (folder.IsExternalImport) { external = folder; continue; }
                int count = 0;
                foreach (var music in batch)
                    if (LibraryPath.IsWithin(music.Path, folder.Path)) count++;
                folder.SongCount += count;
            }
            if (external is null) return;
            int externalCount = 0;
            foreach (var music in batch)
            {
                bool owned = true;
                foreach (var folder in FolderList)
                {
                    if (folder.IsExternalImport) continue;
                    if (LibraryPath.IsWithin(music.Path, folder.Path)) { owned = false; break; }
                }
                if (owned) externalCount++;
            }
            if (externalCount > 0) external.SongCount += externalCount;
        });
}
