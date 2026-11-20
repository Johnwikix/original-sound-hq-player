using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.Helper;
using Microsoft.Extensions.Logging;
namespace WinUIMusicPlayer.ViewModel;

/// <summary>转换交互与进度的唯一归属，不依赖音乐浏览页面实例。</summary>
public sealed class AudioConversionViewModel(AudioConverterService ConverterService, AppViewModel AppViewModel, ApplicationTasks tasks, MusicDatabaseService database)
{
    private ProgressDialog? ProgressDialog;
    private int ProgressBarValue;
    private bool _busy;
    public Task ConvertAsync(IEnumerable<Music> music, string? tag)
    {
        var snapshot = new List<Music>(music);
        if (snapshot.Exists(static item => item.IsRemote))
        {
            AppViewModel.InfoBarTitle = ToolUtils.GetString("Error");
            AppViewModel.InfoBarMessage = ToolUtils.GetString("WebDavReadOnly");
            AppViewModel.InfoBarIsOpen = true;
            return Task.CompletedTask;
        }
        return tasks.RunAsync(_ => ConvertTrackedAsync(snapshot, tag));
    }

    private async Task ConvertTrackedAsync(List<Music> music, string? tag)
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

    private async Task ConvertCoreAsync(List<Music> musicList, string? tag)
    {
        if (musicList.Count == 0 || tag is null)
            return;

        // tag 形如 "wav"（无损直转）或 "mp3:320"（有损格式:码率）
        string[] parts = tag.Split(':');
        string targetFormat = parts[0].ToLowerInvariant();
        int bitrate = parts.Length > 1 && int.TryParse(parts[1], out var b) && b > 0 ? b : 320;

        ProgressBarValue = 0;
        bool committed = false;
        void OnCommitted() => committed = true;
        try
        {
            if (musicList.Count > 1) await ConvertMultipleFiles(musicList, targetFormat, bitrate, OnCommitted);
            else await ConvertSingleFile(musicList[0], targetFormat, bitrate, OnCommitted);
        }
        finally
        {
            if (committed && AppViewModel.CanPublishState)
                await database.PublishLibrarySnapshotAsync(default, preserveRows: true);
        }
    }

    private async Task ConvertMultipleFiles(List<Music> musics, string targetFormat, int bitrate, Action onCommitted)
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
                if (!await ConverterService.ConvertAudioAsync(music, targetFormat, bitrate, onCommitted))
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
    }

    private async Task ConvertSingleFile(Music music, string targetFormat, int bitrate, Action onCommitted)
    {
        if (music.Extension.Equals(targetFormat, StringComparison.OrdinalIgnoreCase))
        {
            UpdateInfoBar(ToolUtils.GetString("InfoBarMessageConverter"));
            return;
        }

        _ = ProgressDialog.UpdateProgress(ProgressBarValue);
        // 先启动转换再决定是否弹出进度对话框（瞬间完成的小文件不闪框）
        Task<bool> convertTask = ConverterService.ConvertAudioAsync(music, targetFormat, bitrate, onCommitted);
        if (ProgressBarValue < 100)
        {
            _ = ProgressDialog.ShowThemedAsync(App.MainWindow.Content.XamlRoot);
        }
        if (!await convertTask)
        {
            UpdateInfoBar(string.Format(ToolUtils.GetString("InfoBarMessageConverterFailedWithFile"), music.Title));
        }
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
