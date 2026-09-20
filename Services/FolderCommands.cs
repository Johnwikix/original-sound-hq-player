using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 文件夹条目命令（与 MusicCommands 同模式）：供 DataTemplate 内 x:Bind 使用，
    /// 实际逻辑在 AddFolderViewModel，经 DI 单例解析。
    /// </summary>
    public static class FolderCommands
    {
        public static IRelayCommand<Folder> OpenFolderCommand { get; } = new RelayCommand<Folder>(OpenFolder);
        public static IAsyncRelayCommand<Folder> RescanFolderCommand { get; } = new AsyncRelayCommand<Folder>(RescanFolderAsync, CanModify);
        public static IAsyncRelayCommand<Folder> RemoveFolderCommand { get; } = new AsyncRelayCommand<Folder>(RemoveFolderAsync, CanModify);

        private static bool CanModify(Folder? folder) => folder is not null && !LibraryOperationGate.IsBusy;

        internal static void NotifyCanExecuteChanged()
        {
            RescanFolderCommand.NotifyCanExecuteChanged();
            RemoveFolderCommand.NotifyCanExecuteChanged();
        }

        private static void OpenFolder(Folder? folder)
        {
            // 外部导入虚拟行无对应磁盘目录，不提供资源管理器入口。
            if (folder is null || folder.IsExternalImport) return;
            _ = App.Services.GetRequiredService<AddFolderViewModel>().OpenFolderAsync(folder.Path);
        }

        private static Task RescanFolderAsync(Folder? folder)
        {
            if (folder is null) return Task.CompletedTask;
            return App.Services.GetRequiredService<AddFolderViewModel>().RescanFolderWithLoadingAsync(folder.Id);
        }

        private static Task RemoveFolderAsync(Folder? folder)
        {
            if (folder is null) return Task.CompletedTask;
            return App.Services.GetRequiredService<AddFolderViewModel>().RemoveFolderWithLoadingAsync(folder.Id);
        }
    }
}
