using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.ViewModel;

internal static class EditorRecoveryChecks
{
    public static async Task RunAsync(SQLiteAsyncConnection db, MusicDatabaseService database, LyricsParser parser, ApplicationTasks tasks)
    {
        var failures = new List<string>();
        async Task Scenario(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
        }
        var online = new LyricsOnlineSearch();
        var resolver = new LyricsRefreshService(database, parser, online, new(), NullLogger<LyricsRefreshService>.Instance);
        string root = Path.Combine(Path.GetTempPath(), "lyrics-editor-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        await Scenario("metadata with unchanged unknown lyrics", async () =>
        {
            var music = new Music { Id = 2, Path = Path.Combine(root, "unknown.flac") };
            await db.InsertAsync(music);
            await db.InsertAsync(new MusicLyrics { MusicId = 2, Lyrics = "Untimed original retained by migration" });
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks);
            await editor.LoadAsync();
            var before = await database.Lyrics.GetAsync(2);
            music.Title = "Only the title changed";
            Check(await editor.SaveAsync(), "metadata save must succeed");
            Check((await db.FindAsync<Music>(2)).Title == music.Title, "edited title must be persisted");
            var after = await database.Lyrics.GetAsync(2);
            Check(after.Document == before.Document && after.SourceKind == before.SourceKind && after.Diagnostic == before.Diagnostic,
                "unchanged raw lyrics, provenance and diagnostic must survive metadata save");
            Check(await editor.SaveAsync(), "a second metadata save uses the new revision");
            editor.OriginalText = "New invalid draft";
            Check(!await editor.SaveAsync() && editor.OriginalText == "New invalid draft", "new invalid lyrics must still be rejected without losing the draft");
        });

        await Scenario("recover invalid translation as raw draft", async () =>
        {
            var music = new Music { Id = 3, Path = Path.Combine(root, "recovery.flac") };
            const string original = "[1000,1000]<0,1000,0>Original";
            const string translation = "Translation without timestamps";
            await db.InsertAsync(music);
            await db.InsertAsync(new MusicLyrics { MusicId = 3, Krc = original, TKrc = translation,
                TranslatedLyrics = "Orphan translation" });
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks);
            await editor.LoadAsync();
            await editor.RestoreLegacyCommand.ExecuteAsync("Krc");
            Check(editor.OriginalText == original && editor.TranslationText == translation && editor.Error.Length > 0,
                "failed normalization must expose both raw fields and a diagnostic");
            Check(!await editor.SaveAsync() && editor.TranslationText == translation, "invalid restored draft must remain available for repair");
            editor.TranslationText = "[00:01.000]Repaired translation";
            Check(await editor.SaveAsync(), "repaired draft can be saved");
            Check((await database.Lyrics.GetAsync(3)).Document.TranslationLrc == "[00:01.000]Repaired translation\n", "repaired translation is normalized");
            await editor.RestoreLegacyCommand.ExecuteAsync("Lrc");
            Check(editor.OriginalText == "" && editor.TranslationText == "Orphan translation", "orphan legacy translation is also recoverable");
            Check(!await editor.SaveAsync(), "orphan translation cannot silently become active lyrics");
            Check((await db.FindAsync<MusicLyrics>(3)).TKrc == translation, "recovery never rewrites the legacy pair");
        });

        await Scenario("external explicit clear and manual refresh", async () =>
        {
            var music = new Music { Path = Path.Combine(root, "external.flac") };
            var downloaded = parser.Import("[00:01.000]Downloaded");
            OneShotLyricsCache.Save(music.Path, downloaded);
            online.Handler = (_, _, _) => Task.FromResult<LyricsDocument?>(downloaded);
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks);
            await editor.LoadAsync();
            editor.OriginalText = "";
            editor.TranslationText = "";
            Check(await editor.SaveAsync(), "external clear must save");
            int calls = online.Calls;
            Check((await resolver.SetLyrics(music, default)).Count == 0 && online.Calls == calls,
                "automatic loading must respect the saved clear");
            await editor.RefreshCommand.ExecuteAsync(null);
            Check(online.Calls == calls + 1 && await editor.SaveAsync(), "explicit refresh remains available after clear");
            Check((await resolver.SetLyrics(music, default)).Count == 1 && online.Calls == calls + 1,
                "manually refreshed content is reused");
        });

        await Scenario("external clear during an automatic download", async () =>
        {
            var music = new Music { Path = Path.Combine(root, "late.flac") };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
            online.Handler = (_, _, _) => { entered.TrySetResult(); return response.Task; };
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks);
            await editor.LoadAsync();
            var loading = resolver.SetLyrics(music, default);
            await entered.Task;
            // The user explicitly edits and clears an initially empty draft.
            editor.OriginalText = "[00:01.000]Temporary edit";
            editor.OriginalText = "";
            Check(await editor.SaveAsync(), "clear during download must save");
            response.SetResult(parser.Import("[00:01.000]Late result"));
            Check((await loading).Count == 0, "late download cannot overwrite clear");
            int calls = online.Calls;
            Check((await resolver.SetLyrics(music, default)).Count == 0 && online.Calls == calls,
                "future loads also respect the clear");
        });
        await Scenario("manual llm translation saves its draft", async () =>
        {
            var music = new Music { Id = 4, Path = Path.Combine(root, "manual-llm.flac") };
            await db.InsertAsync(music);
            var llm = new TestLlmTranslationService { Result = "[00:01.000]Translated" };
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks, llm);
            await editor.LoadAsync();
            editor.OriginalText = "[00:01.000]Original";
            await editor.TranslateCommand.ExecuteAsync(null);
            Check(editor.TranslationText == llm.Result, "manual llm result must return to the draft");
            Check(await editor.SaveAsync(), "manual llm draft must save successfully");
            Check((await database.Lyrics.GetAsync(music.Id)).Document.TranslationLrc == llm.Result + "\n",
                "manual llm translation must be persisted");
        });

        await Scenario("manual llm translation does not overwrite a newer draft", async () =>
        {
            var music = new Music { Id = 5, Path = Path.Combine(root, "manual-llm-conflict.flac") };
            await db.InsertAsync(music);
            var llm = new TestLlmTranslationService
            {
                Pending = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var editor = new LyricsEditorViewModel(music, database, parser, online, tasks, llm);
            await editor.LoadAsync();
            editor.OriginalText = "[00:01.000]Original";
            var translating = editor.TranslateCommand.ExecuteAsync(null);
            await llm.Started.Task;
            Check(editor.IsBusy && !editor.CanSave, "saving must be disabled while manual llm work is pending");
            editor.OriginalText = "[00:01.000]New draft";
            llm.Pending.SetResult("[00:01.000]Stale translation");
            await translating;
            Check(editor.OriginalText.Contains("New draft") && editor.TranslationText.Length == 0 && editor.Error.Length > 0,
                "a late llm result must not replace a newer draft");
        });

        if (failures.Count > 0) throw new Exception(string.Join("\n", failures));
    }

    private sealed class TestLlmTranslationService : ILlmTranslationService
    {
        public string? Result { get; init; }
        public TaskCompletionSource<string?>? Pending { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> TranslateAsync(Music music, LyricsDocument document, CancellationToken token = default)
            => Task.FromResult<string?>(null);

        public async Task<string?> TranslateManuallyAsync(Music music, LyricsDocument document, CancellationToken token = default)
        {
            Started.TrySetResult();
            if (Pending is not null) return await Pending.Task.WaitAsync(token);
            return Result;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
