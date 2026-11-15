using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        UsbExportResult? result = null;
        try
        {
            await _gate.WaitAsync(token);
            entered = true;
            devices.CancelScanForExport();
            state.Operations.Begin(key, ToolUtils.GetString("ProgressTransmitting"));
            // 文件 I/O 只占 95%；发送记录提交成功前不显示 100%。
            var progress = new Progress<double>(p => state.Operations.Report(key, Math.Clamp(p, 0, 100) * 0.95));
            var writer = new UsbWriterHelper(converter, database);
            var dispatcher = App.MainWindow.DispatcherQueue;
            var records = new List<UsbDeviceMusic>(songs.Count);
            async Task OnFileCopied(Music music, string extension)
            {
                await dispatcher.EnqueueAsync(() =>
                {
                    var record = MusicDatabaseService.CreateUsbExportRecord(music, extension, device);
                    records.Add(record);
                    devices.AddSentRecords(new[] { record }, device);
                });
            }
            result = await Task.Run(() => writer.WriteToUsb(songs, device, format, bitRateKbps, progress,
                cancellationToken: token, onFileCopied: OnFileCopied));
            await database.SaveUsbExportRecordsAsync(records, device);
            if (!token.IsCancellationRequested)
            {
                if (result.HasFailures) ReportResult(device, result);
                else state.Operations.Report(key, 100);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "USB 导出失败");
            if (result is null) ReportFailure(device, ex);
            else ReportResult(device, result, ex);
        }
        finally
        {
            state.Operations.Complete(key);
            if (entered) _gate.Release();
        }
    }

    private void ReportResult(UsbStorageDevice device, UsbExportResult result, Exception? statusError = null)
    {
        if (!state.Lifecycle.IsReady) return;
        var message = new StringBuilder();
        message.AppendFormat(ToolUtils.GetString("UsbExportSummary"), device.Name, result.Succeeded.Count,
            result.RequestedCount, result.UnsentCount, result.LyricsFailureCount);
        if (statusError is not null || result.StatusFailureCount > 0)
            message.AppendLine().Append(ToolUtils.GetString("UsbExportStatusFailed"));
        if (statusError is not null) message.AppendLine().Append(statusError.Message);
        if (result.Failures.Count > 0)
        {
            var examples = new StringBuilder();
            for (int i = 0; i < Math.Min(3, result.Failures.Count); i++)
            {
                if (i > 0) examples.AppendLine();
                var failure = result.Failures[i];
                examples.Append(failure.Music.Title).Append(" (").Append(Path.GetFileName(failure.Music.Path))
                    .Append("): ").Append(failure.Message);
            }
            message.AppendLine().AppendFormat(ToolUtils.GetString("UsbExportFailureExamples"), examples);
        }
        state.Shell.InfoBarTitle = ToolUtils.GetString("UsbExportIncomplete");
        state.Shell.InfoBarMessage = message.ToString();
        state.Shell.InfoBarIsOpen = true;
    }

    private void ReportFailure(UsbStorageDevice device, Exception error)
    {
        if (!state.Lifecycle.IsReady) return;
        state.Shell.InfoBarTitle = ToolUtils.GetString("Error");
        state.Shell.InfoBarMessage = string.Format(ToolUtils.GetString("UsbExportFailed"), device.Name, error.Message);
        state.Shell.InfoBarIsOpen = true;
    }
}
