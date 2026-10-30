using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;

/// <summary>编译真实工厂，覆盖封面来源的空结果契约、取消传播与实际任务收尾。</summary>
internal static class EditorCoverChecks
{
    /// <summary>检查真实工厂的封面空状态和离页取消，并可靠收尾测试任务。</summary>
    internal static async Task RunAsync(MusicDatabaseService database, AppViewModel app, Action<bool, string> check)
    {
        var tasks = new ApplicationTasks(app.State.Lifecycle);
        var online = new LrcService();
        using var services = new ServiceCollection()
            .AddSingleton(database).AddSingleton(app).AddSingleton(tasks).AddSingleton(online)
            .AddSingleton<LyricsParser>().AddSingleton<LyricsOnlineSearch>().AddSingleton<EditorSessions>()
            .AddSingleton<ILlmTranslationService>(new NoopTranslation())
            .AddSingleton<LibraryProjectionService>().AddSingleton<NotificationService>()
            .AddSingleton<ILogger<MusicDetailsEditorViewModel>>(NullLogger<MusicDetailsEditorViewModel>.Instance)
            .BuildServiceProvider();
        var factory = new MusicDetailsEditorFactory(services);
        var music = new Music { Path = Path.Combine(AppContext.BaseDirectory, "cover-boundary.flac"), Title = "Cover boundary" };
        ToolUtils.EditorCoverTarget = music;
        try
        {
            await CheckResultsAsync();
            await CheckCancellationAsync(false);
            await CheckCancellationAsync(true);
        }
        finally
        {
            ToolUtils.EditorCoverTarget = null;
            ToolUtils.EditorCoverRead = null;
            await tasks.DrainAsync();
        }

        async Task CheckResultsAsync()
        {
            var editor = factory.Create(music);
            byte[]? bytes = null;
            ToolUtils.EditorCoverRead = _ => Task.FromResult(bytes);
            try
            {
                foreach (var absent in new byte[]?[] { null, [] })
                {
                    bytes = absent;
                    await editor.LoadCommand.ExecuteAsync(null);
                    check(!editor.LoadFailed && editor.Error.Length == 0 && editor.CoverPreview is null
                        && editor.SaveDatabaseCommand.CanExecute(null) && !editor.ExportCoverCommand.CanExecute(null),
                        $"real editor factory accepts {(absent is null ? "null" : "empty bytes")} as a normal missing cover");
                }
                bytes = [1, 2, 3];
                await editor.LoadCommand.ExecuteAsync(null);
                check(editor.LoadFailed && editor.Error == ToolUtils.GetString("FailedObtainCover"),
                    "nonempty invalid cover bytes still report a decoding failure");
                ToolUtils.PrepareArtwork(music.Id);
                ToolUtils.EditorCoverRead = null;
                bytes = await ToolUtils.GetRawImage(music);
                ToolUtils.EditorCoverRead = _ => Task.FromResult(bytes);
                await editor.LoadCommand.ExecuteAsync(null);
                var validPreview = editor.CoverPreview;
                check(!editor.LoadFailed && editor.Error.Length == 0 && validPreview is not null
                    && editor.ExportCoverCommand.CanExecute(null), "valid cover retry clears the error and enables export");
                foreach (var missing in new byte[]?[] { null, [], [1, 2, 3] })
                {
                    online.Handler = _ => Task.FromResult(missing);
                    await editor.FindCoverCommand.ExecuteAsync(null);
                    check(!editor.LoadFailed && editor.Error == ToolUtils.GetString("FailedObtainCover")
                        && ReferenceEquals(validPreview, editor.CoverPreview),
                        $"explicit cover search rejects {(missing is null ? "null" : missing.Length == 0 ? "empty bytes" : "invalid bytes")} and preserves the staged image");
                }
            }
            finally { await editor.StopAsync(); }
        }

        async Task CheckCancellationAsync(bool findOnline)
        {
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<byte[]?> ReadAsync(CancellationToken token)
            {
                entered.TrySetResult(token);
                try { return await response.Task.WaitAsync(token); }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await cleanup.Task;
                    throw;
                }
            }
            ToolUtils.EditorCoverRead = findOnline ? _ => Task.FromResult<byte[]?>([]) : ReadAsync;
            online.Handler = ReadAsync;
            var editor = factory.Create(music);
            Task? operation = null;
            try
            {
                if (findOnline)
                {
                    await editor.LoadCommand.ExecuteAsync(null);
                    operation = editor.FindCoverCommand.ExecuteAsync(null);
                }
                else operation = editor.LoadCommand.ExecuteAsync(null);
                var received = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                editor.BeginClose();
                var stopping = editor.StopAsync();
                string source = findOnline ? "online cover search" : "initial cover read";
                check(received.CanBeCanceled && received.IsCancellationRequested,
                    $"real editor host forwards navigation cancellation to the {source}");
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(!stopping.IsCompleted, $"editor stop still waits for actual {source} cleanup after cancellation");
                cleanup.TrySetResult();
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                response.TrySetResult([]);
                cleanup.TrySetResult();
                if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
                await editor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>隔离本回归不执行的歌词翻译来源。</summary>
    private sealed class NoopTranslation : ILlmTranslationService
    {
        /// <inheritdoc/>
        public Task<string?> TranslateManuallyAsync(Music music, LyricsDocument document, CancellationToken token)
            => Task.FromResult<string?>(null);
    }
}
