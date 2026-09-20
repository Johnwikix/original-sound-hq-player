using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.Services;
using Windows.Storage;
using ModernPicker = Microsoft.Windows.Storage.Pickers.FolderPicker;
using LegacyPicker = Windows.Storage.Pickers.FolderPicker;

internal static class FolderAccessRegression
{
    public static async Task RunAsync()
    {
        var modern = ModernPicker.Pick;
        var legacy = LegacyPicker.Pick;
        try
        {
            var access = new FolderAccessService(NullLogger<FolderAccessService>.Instance);
            var window = new Microsoft.UI.WindowId(1);
            int fallbacks = 0;
            LegacyPicker.Pick = () => { fallbacks++; return Task.FromResult<StorageFolder?>(new("C:\\Music")); };
            var pending = new TaskCompletionSource<Microsoft.Windows.Storage.Pickers.PickFolderResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ModernPicker.Pick = () => pending.Task;
            var picking = access.PickAsync(window, new IntPtr(1));
            if (picking.IsCompleted) throw new Exception("Picker did not await native operation");
            pending.SetResult(null);
            if (await picking is not null || fallbacks != 0) throw new Exception("Cancel opened fallback picker");
            ModernPicker.Pick = () => Task.FromResult<Microsoft.Windows.Storage.Pickers.PickFolderResult?>(new() { Path = "C:\\Music" });
            if ((await access.PickAsync(window, new IntPtr(1)))?.Path != "C:\\Music" || fallbacks != 0)
                throw new Exception("Modern picker result lost");
            ModernPicker.Pick = () => Task.FromException<Microsoft.Windows.Storage.Pickers.PickFolderResult?>(new COMException("Unavailable"));
            if ((await access.PickAsync(window, new IntPtr(1)))?.Path != "C:\\Music" || fallbacks != 1)
                throw new Exception("COM failure did not use fallback");
            LegacyPicker.Pick = () => Task.FromResult<StorageFolder?>(null);
            if (await access.PickAsync(window, new IntPtr(1)) is not null) throw new Exception("Fallback cancellation lost");
            LegacyPicker.Pick = () => Task.FromException<StorageFolder?>(new IOException("Unavailable"));
            try
            {
                await access.PickAsync(window, new IntPtr(1));
                throw new Exception("Fallback failure was swallowed");
            }
            catch (IOException) { }
            Console.WriteLine("PASS: async picker completion, cancellation, COM fallback and fallback failure.");
        }
        finally
        {
            ModernPicker.Pick = modern;
            LegacyPicker.Pick = legacy;
        }
    }
}
