using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
namespace WinUIMusicPlayer.Services;

/// <summary>歌词解析的代次所有者；退出及切歌后不发布过期结果，实际任务由 ApplicationTasks 排空。</summary>
public sealed class LyricsLoader(AppState state, LyricsRefreshService parser, ApplicationTasks tasks, ILogger<LyricsLoader> logger) : IDisposable
{
    private const int DebounceMilliseconds = 500;
    private CancellationTokenSource? _request;
    private bool _disposed;
    private int _ticket;
    public void Load(Music music)
    {
        if (_disposed || state.Lifecycle.Phase == AppPhase.Stopping) return;
        var dispatcher = App.MainWindow.DispatcherQueue;
        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => Load(music));
            return;
        }
        state.Presentation.LastLyricIndex = -1;
        int ticket = Interlocked.Increment(ref _ticket);
        _request?.Cancel();
        var owner = new CancellationTokenSource();
        _request = owner;
        var snapshot = new Music { Id = music.Id, SourceId = music.SourceId, Path = music.Path, Title = music.Title,
            Author = music.Author, Album = music.Album, Duration = music.Duration, EmbeddedLyrics = music.EmbeddedLyrics,
            IsLrcSearched = music.IsLrcSearched, IsKrcSearched = music.IsKrcSearched };
        _ = RunOwnedAsync();
        async Task RunOwnedAsync()
        {
            try
            {
                await tasks.RunAsync(async token =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.Token);
                    bool finalPublished = false;
                    try
                    {
                        // Coalesce track changes before any cache, disk or online lookup starts.
                        // The request owns this delay, so switching or shutdown cancels it immediately.
                        await Task.Delay(DebounceMilliseconds, linked.Token);
                        var lyrics = await Task.Run(() => parser.SetLyrics(snapshot, linked.Token, cached =>
                            dispatcher.TryEnqueue(() =>
                            {
                                if (!finalPublished && !_disposed && state.Lifecycle.Phase != AppPhase.Stopping && !owner.IsCancellationRequested && ticket == Volatile.Read(ref _ticket))
                                    state.Presentation.UILyrics = EnsureDisplayLyrics(cached, snapshot.Duration);
                            })), linked.Token);
                        finalPublished = true;
                        if (!_disposed && state.Lifecycle.Phase != AppPhase.Stopping && !linked.IsCancellationRequested && ticket == Volatile.Read(ref _ticket))
                            state.Presentation.UILyrics = EnsureDisplayLyrics(lyrics, snapshot.Duration);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                    catch (Exception ex) { logger.LogError(ex, "加载歌词失败"); }
                });
            }
            catch (Exception ex) { logger.LogError(ex, "歌词任务结束失败"); }
            finally
            {
                if (ReferenceEquals(_request, owner)) _request = null;
                owner.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _ticket);
        _request?.Cancel();
    }

    private static List<LyricLine> EnsureDisplayLyrics(List<LyricLine> lyrics, TimeSpan duration)
    {
        if (lyrics.Count > 0) return lyrics;

        double endMs = duration.TotalMilliseconds > 0 ? duration.TotalMilliseconds + 2000 : 10500;
        var placeholder = new LyricLine
        {
            IsCurrent = true,
            StartMs = 0,
            EndMs = endMs,
            HighlightEndMs = endMs,
        };
        placeholder.Words.Add(new LyricWord
        {
            Word = ToolUtils.GetString("LyricsGetFailed"),
            StartMs = 0,
            DurationMs = 0,
        });
        return [placeholder];
    }
}
