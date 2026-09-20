using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace WinUIMusicPlayer.Services;

/// <summary>封装 Windows 文件夹选择与打开；窗口交互由 UI 线程调用。</summary>
public sealed class FolderAccessService(ILogger<FolderAccessService> logger)
{
    /// <summary>取消返回 null；仅在新选择器不可用时尝试 HWND 绑定的系统选择器。</summary>
    public async Task<StorageFolder?> PickAsync(Microsoft.UI.WindowId windowId, IntPtr owner)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);
            var result = await picker.PickSingleFolderAsync();
            path = result?.Path;
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            logger.LogWarning(ex, "文件夹选择器不可用，尝试系统兼容选择器");
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
            return await picker.PickSingleFolderAsync();
        }

        // 取消不能触发第二个对话框；路径解析失败也不应重新弹选择器。
        return path is null ? null : await StorageFolder.GetFolderFromPathAsync(path);
    }

    /// <summary>直接按路径打开目录，系统启动器失败时回退到资源管理器。</summary>
    public async Task OpenAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            if (await Launcher.LaunchFolderPathAsync(fullPath)) return;
            logger.LogWarning("系统启动器未打开文件夹，尝试资源管理器");
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException)
        {
            logger.LogWarning(ex, "系统启动器打开文件夹失败，尝试资源管理器");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false
        };
        // ArgumentList 负责路径转义，避免空格、中文或命令字符改变参数边界。
        start.ArgumentList.Add(fullPath);
        using var process = Process.Start(start);
        if (process is null) throw new IOException("Unable to start File Explorer.");
    }
}
