using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
namespace WinUIMusicPlayer.Services;

/// <summary>库条目的网络歌词和扫描用例；固定输入后登记实际任务，退出不释放仍被使用的依赖。</summary>
public sealed class LibraryTrackActions(AppState state, MusicDatabaseService database, ApplicationTasks tasks, ILogger<LibraryTrackActions> logger, WinUIMusicPlayer.Services.Lyrics.LyricsOnlineSearch lyricsSearch)
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
                    try
                    {
                        var stored = music.Id > 0 ? await database.Lyrics.GetAsync(music.Id, token) : null;
                        long externalRevision = stored is null ? OneShotLyricsCache.Load(music.Path)?.Revision ?? 0 : 0;
                        var document = await lyricsSearch.SearchAsync(music, true, token);
                        token.ThrowIfCancellationRequested();
                        if (document is null) { Report(new InvalidOperationException(ToolUtils.GetString("FailedObtainLyrics"))); continue; }
                        if (stored is null)
                        {
                            if (!OneShotLyricsCache.SaveEdited(music.Path, document, externalRevision))
                                Report(new InvalidOperationException(ToolUtils.GetString("LyricsEditConflict")));
                        }
                        else if (!await database.Lyrics.SaveAsync(music.Id, document, stored.Revision, "Online", token: token))
                            Report(new InvalidOperationException(ToolUtils.GetString("LyricsEditConflict")));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Report(ex); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Report(ex); }
        });
    }
    public Task RescanAsync(Music music)
    {
        if (!state.Lifecycle.IsReady || string.IsNullOrEmpty(music?.FolderPath)) return Task.CompletedTask;
        if (music.IsRemote)
            return tasks.RunAsync(async token =>
            {
                try
                {
                    var library = App.Services.GetRequiredService<WebDavLibraryService>();
                    var (source, _) = await library.ResolveAsync(music);
                    token.ThrowIfCancellationRequested();
                    await library.ScanAsync(source);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch { state.Shell.InfoBarMessage = ToolUtils.GetString("WebDavErrorConnectionFailed"); state.Shell.InfoBarIsOpen = true; }
            });
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
