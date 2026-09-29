using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

/// <summary>Editor owns its draft and revision; network responses never replace a newer draft.</summary>
public sealed class LyricsEditorViewModel : ObservableObject, IDisposable
{
    private readonly Music _music;
    private readonly MusicDatabaseService _database;
    private readonly LyricsParser _parser;
    private readonly LyricsOnlineSearch _online;
    private readonly ApplicationTasks _tasks;
    private readonly CancellationTokenSource _closed = new();
    private long _revision;
    private long _draftVersion;
    private bool _loaded;
    private bool _disposed;

    public string OriginalText { get => field; set { if (SetProperty(ref field, value)) _draftVersion++; } } = "";
    public string TranslationText { get => field; set { if (SetProperty(ref field, value)) _draftVersion++; } } = "";
    public string Status { get => field; private set => SetProperty(ref field, value); } = "";
    public string Error { get => field; private set => SetProperty(ref field, value); } = "";
    public bool IsBusy { get => field; private set { if (SetProperty(ref field, value)) { RefreshCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); RestoreLegacyCommand.NotifyCanExecuteChanged(); } } }
    public bool HasLegacy { get => field; private set => SetProperty(ref field, value); }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand<string> RestoreLegacyCommand { get; }

    public LyricsEditorViewModel(Music music, MusicDatabaseService database, LyricsParser parser, LyricsOnlineSearch online, ApplicationTasks tasks)
    {
        _music = music;
        _database = database;
        _parser = parser;
        _online = online;
        _tasks = tasks;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _loaded && !IsBusy && !_disposed);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _loaded && !IsBusy && !_disposed && !_music.IsRemote);
        RestoreLegacyCommand = new AsyncRelayCommand<string>(RestoreAsync, _ => _loaded && !IsBusy && !_disposed);
    }

    public Task LoadAsync() => RunAsync(async token =>
    {
        if (_music.Id <= 0)
        {
            var cached = OneShotLyricsCache.Load(_music.Path);
            _revision = cached?.Revision ?? 0;
            if (_draftVersion == 0) SetDocument(cached?.Document ?? _parser.Import(_music.EmbeddedLyrics, token: token));
            _loaded = true;
            return;
        }
        var snapshot = await _database.Lyrics.GetAsync(_music.Id, token);
        token.ThrowIfCancellationRequested();
        _revision = snapshot.Revision;
        if (_draftVersion == 0) SetDocument(snapshot.Document);
        var legacy = await _database.GetDbConnection().FindAsync<MusicLyrics>(_music.Id);
        token.ThrowIfCancellationRequested();
        HasLegacy = legacy is not null;
        Error = snapshot.Diagnostic.Length == 0 ? "" : ToolUtils.GetString("LyricsMigrationDiagnostic");
        _loaded = true;
    });

    public Task ReadEmbeddedAsync() => RunAsync(async token =>
    {
        long draft = _draftVersion;
        string path = _music.Path;
        var result = await Task.Run(async () =>
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            return await ToolUtils.GetMusicInfo(file);
        }, token);
        token.ThrowIfCancellationRequested();
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        SetDocument(_parser.Import(result.Item2, token: token));
    });

    public async Task<bool> SaveAsync(byte[]? cover = null, bool queueMetadata = false)
    {
        bool saved = false;
        if (!_loaded || IsBusy || _disposed) return false;
        await RunAsync(async token =>
        {
            long draft = _draftVersion;
            string original = OriginalText, translation = TranslationText;
            var document = await Task.Run(() => _parser.Import(original, translation, token), token);
            token.ThrowIfCancellationRequested();
            if (draft != _draftVersion) throw new InvalidOperationException("LyricsEditConflict");
            if (!string.IsNullOrWhiteSpace(original) && !_parser.HasLyrics(document, token)) throw new FormatException("Invalid original lyrics.");
            if (string.IsNullOrWhiteSpace(original) && !string.IsNullOrWhiteSpace(translation)) throw new FormatException("Translation requires original lyrics.");
            if (_music.Id <= 0)
            {
                if (queueMetadata) throw new InvalidOperationException("External tracks must be imported before queuing tag edits.");
                if (!OneShotLyricsCache.SaveEdited(_music.Path, document, _revision)) throw new InvalidOperationException("LyricsEditConflict");
            }
            else if (queueMetadata)
                await _database.QueueMetadataWriteAsync(_music, cover, document, _revision, token);
            else await _database.SaveDetailsAsync(_music, document, _revision, token);
            _revision++;
            saved = draft == _draftVersion;
            if (!saved && !_disposed) Error = ToolUtils.GetString("LyricsEditConflict");
        });
        return saved;
    }

    private Task RefreshAsync() => RunAsync(async token =>
    {
        long draft = _draftVersion;
        var document = await _online.SearchAsync(_music, true, token);
        token.ThrowIfCancellationRequested();
        if (document is null) { Error = ToolUtils.GetString("FailedObtainLyrics"); return; }
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        SetDocument(document);
    });

    private Task ExportAsync() => RunAsync(async token =>
    {
        string original = OriginalText, translation = TranslationText;
        var document = await Task.Run(() => _parser.Import(original, translation, token), token);
        string path = await LyricsExporter.SaveFilesAsync(_music.Path, document, token);
        if (!_disposed) Status = ToolUtils.GetString("LyricsFilesSaved") + " " + path;
    });

    private Task RestoreAsync(string? slot) => RunAsync(async token =>
    {
        long draft = _draftVersion;
        var legacy = await _database.GetDbConnection().FindAsync<MusicLyrics>(_music.Id);
        if (legacy is null) return;
        var document = await Task.Run(() => _parser.Import(slot == "Krc" ? legacy.Krc : legacy.Lyrics,
            slot == "Krc" ? legacy.TKrc : legacy.TranslatedLyrics, token), token);
        token.ThrowIfCancellationRequested();
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        SetDocument(document); // Recovery is a draft until the user saves it.
    });

    private void SetDocument(LyricsDocument document)
    {
        OriginalText = document.Original.Content;
        TranslationText = document.TranslationLrc ?? "";
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        Error = "";
        Status = "";
        try
        {
            await _tasks.RunAsync(async stopping =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _closed.Token);
                await action(linked.Token);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Error = ToolUtils.GetString(ex.Message == "LyricsEditConflict" ? "LyricsEditConflict" : "LyricsInvalid") + " " + ex.Message; }
        finally
        {
            IsBusy = false;
            if (_disposed) _closed.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closed.Cancel();
        if (!IsBusy) _closed.Dispose();
    }
}
