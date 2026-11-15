using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try
        {
            // 沿用旧互斥量名称，首实例判断先于 App/Host 和音频进程创建。
            using var endpoint = new SingleInstanceEndpoint("WinUIMusicPlayer_SingleInstanceMutex");
            if (!endpoint.IsPrimary)
            {
                if (endpoint.SignalAsync(ActivationFileResolver.CapturePath()).GetAwaiter().GetResult()) return 0;
                throw new IOException("Existing instance did not acknowledge the activation request.");
            }

            endpoint.Listen();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(endpoint);
            });
            return 0;
        }
        catch (Exception ex)
        {
            // Host 尚未创建时也保留诊断；转发失败不能退回启动第二套播放进程。
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OriginalSoundPlayer");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "activation-errors.log"), $"{DateTimeOffset.Now:O} {ex}\r\n");
            }
            catch { }
            return 1;
        }
    }
}
