using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.IO;
using Windows.ApplicationModel.Activation;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper;

internal static class ActivationFileResolver
{
    public static string? CapturePath()
    {
        try
        {
            // AppInstance 仅用于取得 MSIX 文件激活参数，不用于单实例判定或重定向。
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs files)
                foreach (var file in files.Files)
                    if (IsPlayable(file.Path)) return file.Path;
        }
        catch (Exception ex) { Debug.WriteLine($"读取文件激活参数失败: {ex.Message}"); }
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 1; index < args.Length; index++)
            if (IsPlayable(args[index])) return args[index];
        return null;
    }

    private static bool IsPlayable(string path) => path.Length > 0 &&
        MusicFileTypes.Contains(Path.GetExtension(path)) && File.Exists(path);
}
