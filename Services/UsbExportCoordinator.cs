using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>USB 导出的任务与台账边界；退出等待实际 I/O 完成，后台不访问页面。</summary>
public sealed class UsbExportCoordinator(AppState state, ApplicationTasks tasks, AudioConverterService converter,
    MusicDatabaseService database, UsbDeviceService devices, ILogger<UsbExportCoordinator> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task ExportAsync(IEnumerable<Music> selected, UsbStorageDevice device, string? format, int bitRateKbps)
    {
        if (!state.Lifecycle.IsReady) return Task.CompletedTask;
        var snapshot = new List<Music>(selected);
        if (snapshot.Exists(static music => music.IsRemote))
        {
            state.Shell.InfoBarTitle = ToolUtils.GetString("Error");
            state.Shell.InfoBarMessage = ToolUtils.GetString("WebDavReadOnly");
            state.Shell.InfoBarIsOpen = true;
            return Task.CompletedTask;
        }
        return tasks.RunAsync(token => ExportCoreAsync(snapshot, device, format, bitRateKbps, token));
    }

    private async Task ExportCoreAsync(List<Music> songs, UsbStorageDevice device, string? format, int bitRateKbps, CancellationToken token)
    {
        if (songs.Count == 0) return;
        string key = $"{ProgressCenter.Keys.UsbTransmitting}:{Guid.NewGuid():N}";
        bool entered = false;
        try
        {
            await _gate.WaitAsync(token);
            entered = true;
            state.Operations.Begin(key, ToolUtils.GetString("ProgressTransmitting"));
            var progress = new Progress<double>(p => state.Operations.Report(key, p));
            var writer = new UsbWriterHelper(converter, database);
            var succeeded = await Task.Run(() => writer.WriteToUsb(songs, device, format, bitRateKbps, progress, cancellationToken: token));
            devices.AddSentRecords(succeeded, device);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "USB 导出失败"); }
        finally
        {
            state.Operations.Complete(key);
            if (entered) _gate.Release();
        }
    }
}
