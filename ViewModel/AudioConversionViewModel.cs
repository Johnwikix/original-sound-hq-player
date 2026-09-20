using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.Helper;
using ZLinq;
using Microsoft.Extensions.Logging;
namespace WinUIMusicPlayer.ViewModel;

/// <summary>转换交互与进度的唯一归属，不依赖音乐浏览页面实例。</summary>
public sealed class AudioConversionViewModel(AudioConverterService ConverterService, AppViewModel AppViewModel, ApplicationTasks tasks)
{
    private ProgressDialog? ProgressDialog;
    private int ProgressBarValue;
    private bool IsMutiFile;
    private bool _busy;
    public Task ConvertAsync(IEnumerable<Music> music, string? tag)
    {
        var snapshot = new List<Music>(music);
        return tasks.RunAsync(_ => ConvertTrackedAsync(snapshot, tag));
    }

    private async Task ConvertTrackedAsync(IEnumerable<Music> music, string? tag)
    {
        if (_busy || !AppViewModel.IsInitialized) return;
        _busy = true;
        try
        {
            ProgressDialog ??= new ProgressDialog(ToolUtils.GetString("Converting")) { Title = ToolUtils.GetString("Processing") };
            ConverterService.updateProgress += OnConverterProgressUpdated;
            await ConvertCoreAsync(music, tag);
        }
        catch (Exception ex)
        {
            App.GetLogger<AudioConversionViewModel>().LogError(ex, "音频转换失败");
            if (AppViewModel.CanPublishState)
            {
                AppViewModel.InfoBarTitle = ToolUtils.GetString("Error");
                AppViewModel.InfoBarMessage = ex.Message;
                AppViewModel.InfoBarIsOpen = true;
            }
        }
        finally { ConverterService.updateProgress -= OnConverterProgressUpdated; _busy = false; }
    }
    // 批量转换聚合进度：总进度 = (已完成文件数 + 当前文件内部进度) / 总数。
    // 多轮批量转换在每轮 ConvertAsync 里重置。
    private int _batchTotalFiles;
    private int _batchCompletedFiles;
    private double _batchCurrentFilePercent;

    private void OnConverterProgressUpdated(object sender, double progress)
    {
        if (ProgressDialog is null) return;

        _batchCurrentFilePercent = progress;
        if (_batchTotalFiles > 0)
        {
            ProgressBarValue = (int)Math.Clamp(
                (_batchCompletedFiles + _batchCurrentFilePercent / 100.0) * 100.0 / _batchTotalFiles, 0, 100);
            _ = ProgressDialog.UpdateProgress(ProgressBarValue);
            return;
        }

        if (ProgressBarValue < (int)progress)
        {
            ProgressBarValue = (int)progress;
        }
        _ = ProgressDialog.UpdateProgress(ProgressBarValue);
    }

    private async Task ConvertCoreAsync(IEnumerable<Music> uniqueSelectedMusics, string? tag)
    {
        if (uniqueSelectedMusics is null || tag is null)
            return;

        // tag 形如 "wav"（无损直转）或 "mp3:320"（有损格式:码率）
        string[] parts = tag.Split(':');
        string targetFormat = parts[0].ToLowerInvariant();
        int bitrate = parts.Length > 1 && int.TryParse(parts[1], out var b) && b > 0 ? b : 320;

        ProgressBarValue = 0;
        var musicList = uniqueSelectedMusics.AsValueEnumerable().ToList();
        IsMutiFile = musicList.Count > 1;
        if (IsMutiFile)
        {
            await ConvertMultipleFiles(musicList, targetFormat, bitrate);
        }
        else
        {
            await ConvertSingleFile(musicList.AsValueEnumerable().FirstOrDefault(), targetFormat, bitrate);
        }
    }

    private async Task ConvertMultipleFiles(List<Music> musics, string targetFormat, int bitrate)
    {
        await ProgressDialog.UpdateProgress(ProgressBarValue);
        _ = ProgressDialog.ShowThemedAsync(App.MainWindow.Content.XamlRoot);

        _batchTotalFiles = musics.Count;
        _batchCompletedFiles = 0;
        bool allSuccess = true;
        try
        {
            foreach (Music music in musics)
            {
                if (!AppViewModel.IsInitialized) break;
                _batchCurrentFilePercent = 0;
                if (!await ConverterService.ConvertAudioAsync(music, targetFormat, bitrate))
                    allSuccess = false;
                _batchCompletedFiles++;
            }
        }
        finally
        {
            _batchTotalFiles = 0; // 无论成败都退出批量模式，避免污染后续单文件进度
        }
        _ = ProgressDialog.UpdateProgress(100);
        if (!allSuccess)
            UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverterFailed"));
        // 转换产物已主动入库；等写入门清空、watcher 触发的 AutoScan 收尾后统一刷新一次
        await AudioFileWriteGate.WaitUntilClearAsync();
        await AutoRescanService.WaitUntilIdleAsync();
        await AppViewModel.RefreshSongsSourceAsync();
    }

    private async Task ConvertSingleFile(Music? music, string targetFormat, int bitrate)
    {
        if (music is null)
            return;

        if (music.Extension.Equals(targetFormat, StringComparison.OrdinalIgnoreCase))
        {
            UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
            return;
        }

        _ = ProgressDialog.UpdateProgress(ProgressBarValue);
        // 先启动转换再决定是否弹出进度对话框（瞬间完成的小文件不闪框）
        Task<bool> convertTask = ConverterService.ConvertAudioAsync(music, targetFormat, bitrate);
        if (ProgressBarValue < 100)
        {
            _ = ProgressDialog.ShowThemedAsync(App.MainWindow.Content.XamlRoot);
        }
        if (!await convertTask)
        {
            UpdateInfoBar(string.Format(ToolUtils.GetString("InfoBarMessageConverterFailedWithFile"), music.Title));
        }
        // 转换产物已主动入库；等写入门清空、watcher 触发的 AutoScan 收尾后统一刷新一次
        await AudioFileWriteGate.WaitUntilClearAsync();
        await AutoRescanService.WaitUntilIdleAsync();
        await AppViewModel.RefreshSongsSourceAsync();
    }

    public void UpdateInfoBar(string message)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            AppViewModel.InfoBarIsOpen = true;
            AppViewModel.InfoBarTitle = ToolUtils.GetString("InfoBarTitleConverter");
            AppViewModel.InfoBarMessage = message;
        });
    }

}
