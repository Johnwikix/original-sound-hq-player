using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
namespace WinUIMusicPlayer.Services;

/// <summary>歌词解析的代次所有者；退出及切歌后不发布过期结果，实际任务由 ApplicationTasks 排空。</summary>
public sealed class LyricsLoader(AppState state, LyricsRefreshService parser, ApplicationTasks tasks, ILogger<LyricsLoader> logger)
{
    private int _ticket;
    public void Load(Music music, bool countPlayback = true)
    {
        if (state.Lifecycle.Phase == AppPhase.Stopping) return;
        var dispatcher = App.MainWindow.DispatcherQueue;
        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => Load(music, countPlayback));
            return;
        }
        state.Presentation.LastLyricIndex = -1;
        int ticket = Interlocked.Increment(ref _ticket);
        _ = tasks.RunAsync(async token =>
        {
            try
            {
                var lyrics = await Task.Run(() => parser.SetLyrics(music, countPlayback));
                if (!token.IsCancellationRequested && ticket == Volatile.Read(ref _ticket)) state.Presentation.UILyrics = lyrics;
            }
            catch (Exception ex) { logger.LogError(ex, "加载歌词失败"); }
        });
    }
}
