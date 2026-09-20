using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
namespace WinUIMusicPlayer.Services;

/// <summary>库条目的网络歌词和扫描用例；固定输入后登记实际任务，退出不释放仍被使用的依赖。</summary>
public sealed class LibraryTrackActions(AppState state, MusicDatabaseService database, ApplicationTasks tasks, ILogger<LibraryTrackActions> logger)
{
    public Task RefreshLyricsAsync(IEnumerable<Music> songs)
    {
        if (!state.Lifecycle.IsReady || songs is null) return Task.CompletedTask;
        var snapshot = songs.ToArray();
        return tasks.RunAsync(async token =>
        {
            try
            {
                foreach (var music in snapshot)
                {
                    token.ThrowIfCancellationRequested();
                    var (lyrics, translated) = await ToolUtils.GetLyricsFromNet(music);
                    token.ThrowIfCancellationRequested();
                    var (krc, translatedKrc) = await ToolUtils.GetKrcFromNet(music);
                    token.ThrowIfCancellationRequested();
                    await database.SaveLyricsAsync(music.Id, lyrics, translated, krc ?? "", translatedKrc ?? "");
                    await database.UpdateMusicInfo(music);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Report(ex); }
        });
    }
    public Task RescanAsync(Music music)
    {
        if (!state.Lifecycle.IsReady || string.IsNullOrEmpty(music?.FolderPath)) return Task.CompletedTask;
        string path = music.FolderPath;
        return tasks.RunAsync(async token =>
        {
            try { await Task.Run(() => database.RescanFolderByPath(path), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Report(ex); }
        });
    }
    private void Report(Exception ex)
    {
        logger.LogError(ex, "音乐库操作失败");
        if (!state.Lifecycle.IsReady) return;
        state.Shell.InfoBarTitle = ToolUtils.GetString("Error");
        state.Shell.InfoBarMessage = ex.Message;
        state.Shell.InfoBarIsOpen = true;
    }
}
