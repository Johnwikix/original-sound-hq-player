using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.System;
using WinUIEx;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Helper;
namespace WinUIMusicPlayer.Services;

/// <summary>设置页面的平台操作；共享偏好通过 AppState 发布。</summary>
public sealed partial class SettingsActions(AppState state, MusicDatabaseService database, ILogger<SettingsActions> logger)
{
    [RelayCommand]
    private void OnBackdropTypeChanged(string type)
    {
        try
        {
            switch (type)
            {
                case "Acrylic":
                    state.Preferences.BackdropType = "Acrylic";
                    state.Shell.IsColorPickerVisible = false;
                    break;
                case "TransparentAcrylic":
                    state.Preferences.BackdropType = "TransparentAcrylic";
                    state.Shell.IsColorPickerVisible = false;
                    break;
                case "Mica":
                    state.Preferences.BackdropType = "Mica";
                    state.Shell.IsColorPickerVisible = false;
                    break;
                case "TransparentTint":
                    state.Preferences.BackdropType = "TransparentTint";
                    state.Shell.IsColorPickerVisible = false;
                    break;
                case "CustomAcrylicStyle":
                    state.Preferences.BackdropType = "CustomAcrylicStyle";
                    state.Shell.IsColorPickerVisible = true;
                    break;
            }
            App.MainWindow?.SetAppStyle();
            if (state.Lifecycle.IsReady)
            {
                _ = database.SaveSettingAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, ex.Message);
        }
    }
    [RelayCommand]
    private void OnThemeTypeChanged(string type)
    {
        state.Preferences.ThemeType = type;
    }

    [RelayCommand]
    private async Task OpenLogPath()
    {
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Logs");
        var folder = await StorageFolder.GetFolderFromPathAsync(logDirectory);
        var options = new FolderLauncherOptions
        {
            DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
        };
        await Launcher.LaunchFolderAsync(folder, options);
    }

    [RelayCommand]
    private async Task OpenSettingsFolder()
    {
        string settingsDirectory;
        try
        {
            settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Settings");
            if (!Directory.Exists(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }
        }
        catch
        {
            settingsDirectory = ApplicationData.Current.LocalFolder.Path;
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(settingsDirectory);
        var options = new FolderLauncherOptions
        {
            DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
        };
        await Launcher.LaunchFolderAsync(folder, options);
    }

    [RelayCommand]
    private async Task ChangeCoverCacheLocation()
    {
        var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
        PickFolderResult folder = await folderPicker.PickSingleFolderAsync();
        if (folder is not null)
        {
            state.Preferences.MusicCoverCache = folder.Path;
        }
    }

    [RelayCommand]
    private void OpenWebSite()
    {
        _ = Launcher.LaunchUriAsync(new Uri("https://johnwikix.github.io/original-sound-player-page"));
    }

    [RelayCommand]
    private void OpenMainGitHub()
    {
        _ = Launcher.LaunchUriAsync(new Uri("https://github.com/Johnwikix/original-sound-hq-player"));
    }
    [RelayCommand]
    private async Task OpenCoverCacheLocation()
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(state.Preferences.MusicCoverCache);
        var options = new FolderLauncherOptions
        {
            DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
        };
        await Launcher.LaunchFolderAsync(folder, options);
    }

    [RelayCommand]
    private async Task ClearCoverCache()
    {
        try
        {
            string cacheRoot = state.Preferences.MusicCoverCache;
            await Task.Run(() =>
            {
                // 根目录下的 .bin 为网络封面原图缓存
                if (!string.IsNullOrEmpty(cacheRoot) && Directory.Exists(cacheRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(cacheRoot, "*.bin"))
                    {
                        File.Delete(file);
                    }
                }

                // Cache 子目录存放缩略图 .bmp 与全尺寸 _raw.bin，整体删除
                if (!string.IsNullOrEmpty(cacheRoot))
                {
                    var cacheDir = Path.Combine(cacheRoot, "Cache");
                    if (Directory.Exists(cacheDir))
                    {
                        Directory.Delete(cacheDir, recursive: true);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"清空封面缓存失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private static async Task TrimNow()
    {
        await WorkingSetCompressor.TrimSelfAsync();
    }

    [RelayCommand]
    private void ResetWindowBounds()
    {
        App.MainWindow.CenterOnScreen();
    }
}
