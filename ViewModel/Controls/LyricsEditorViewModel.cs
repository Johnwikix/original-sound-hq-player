using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
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
    private readonly ILlmTranslationService? _llm;
    private readonly CancellationTokenSource _closed = new();
    private LyricsEditDraft _draft = new();
    private LyricsDocument _savedDocument { get => _draft.SavedDocument; set => _draft.SavedDocument = value; }
    private long _revision { get => _draft.Revision; set => _draft.Revision = value; }
    private int _ownerId { get => _draft.OwnerId; set => _draft.OwnerId = value; }
    internal int DocumentOwnerId => _ownerId;
    private long _draftVersion { get => _draft.Version; set => _draft.Version = value; }
    private bool _loaded { get => _draft.Loaded; set => _draft.Loaded = value; }
    private bool _disposed;
    private Task _operationTask = Task.CompletedTask;

    public string OriginalText { get => _draft.OriginalText; set => _draft.OriginalText = value; }
    public string TranslationText { get => _draft.TranslationText; set => _draft.TranslationText = value; }
    public string PronunciationText { get => _draft.PronunciationText; set => _draft.PronunciationText = value; }
    /// <summary>获取文本是否包含未提交修改。</summary>
    public bool HasChanges => _loaded && (OriginalText != _savedDocument.Original.Content ||
        TranslationText != (_savedDocument.TranslationLrc ?? "") || PronunciationText != (_savedDocument.PronunciationLrc ?? ""));
    public string Status { get => field; private set => SetProperty(ref field, value); } = "";
    public string Error { get => field; private set => SetProperty(ref field, value); } = "";
    public bool IsBusy
    {
        get => _draft.IsBusy;
        private set => _draft.IsBusy = value;
    }
    private void DraftChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        OnPropertyChanged(args.PropertyName);
        OnPropertyChanged(nameof(HasChanges));
        if (args.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanSaveFile));
            RefreshCommand.NotifyCanExecuteChanged();
            ExportCommand.NotifyCanExecuteChanged();
            RestoreLegacyCommand.NotifyCanExecuteChanged();
            TranslateCommand.NotifyCanExecuteChanged();
        }
    }
    public bool CanSave => _loaded && !IsBusy && !_disposed;
    public bool CanSaveFile => CanSave && !_music.IsRemote;
    public bool HasLegacy { get => field; private set => SetProperty(ref field, value); }
    /// <summary>获取最近一次已提交事务的标签快照；歌词在保存期间继续编辑时也可发布已保存的标签。</summary>
    public Music? LastCommittedMetadata { get; private set; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand TranslateCommand { get; }
    public IAsyncRelayCommand<string> RestoreLegacyCommand { get; }

    public LyricsEditorViewModel(Music music, MusicDatabaseService database, LyricsParser parser, LyricsOnlineSearch online,
        ApplicationTasks tasks, ILlmTranslationService? llm = null)
    {
        _music = music;
        _database = database;
        _parser = parser;
        _online = online;
        _tasks = tasks;
        _llm = llm;
        _draft.PropertyChanged += DraftChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _loaded && !IsBusy && !_disposed);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _loaded && !IsBusy && !_disposed && !_music.IsRemote);
        TranslateCommand = new AsyncRelayCommand(TranslateAsync, () => _loaded && !IsBusy && !_disposed && _llm is not null);
        RestoreLegacyCommand = new AsyncRelayCommand<string>(RestoreAsync, _ => _loaded && !IsBusy && !_disposed);
    }

    public Task LoadAsync() => _loaded ? _draft.Operation : RunAsync(async token =>
    {
        if (_music.Id <= 0)
        {
            var cached = OneShotLyricsCache.Load(_music.Path);
            _revision = cached?.Revision ?? 0;
            _savedDocument = cached?.Document ?? _parser.Import(_music.EmbeddedLyrics, token: token);
            if (_draftVersion == 0) SetDocument(_savedDocument);
            _loaded = true;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanSaveFile));
            TranslateCommand.NotifyCanExecuteChanged();
            return;
        }
        var snapshot = await _database.Lyrics.GetAsync(_music.Id, token);
        token.ThrowIfCancellationRequested();
        _revision = snapshot.Revision;
        _ownerId = snapshot.OwnerId;
        _savedDocument = snapshot.Document;
        if (_draftVersion == 0) SetDocument(snapshot.Document);
        var legacy = await _database.GetDbConnection().FindAsync<MusicLyrics>(_music.Id);
        token.ThrowIfCancellationRequested();
        HasLegacy = legacy is not null;
        Error = snapshot.Diagnostic.Length == 0 ? "" : ToolUtils.GetString("LyricsMigrationDiagnostic");
        _loaded = true;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSaveFile));
        TranslateCommand.NotifyCanExecuteChanged();
    });

    /// <summary>复用同组默认歌词草稿；版本覆盖仍保持独立。</summary>
    public void ShareDraftWith(LyricsEditorViewModel source)
    {
        if (ReferenceEquals(_draft, source._draft) || _music.HasVersionLyrics || source._music.HasVersionLyrics ||
            _music.SongId <= 0 || _music.SongId != source._music.SongId || HasChanges) return;
        _draft.PropertyChanged -= DraftChanged;
        _draft = source._draft;
        _draft.PropertyChanged += DraftChanged;
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(TranslationText));
        OnPropertyChanged(nameof(PronunciationText));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>归属修改后仅重读没有未保存输入的文档。</summary>
    public async Task ReloadIfUnchangedAsync()
    {
        await WhenIdleAsync();
        if (_disposed) return;
        if (HasChanges)
        {
            var saved = await _database.Lyrics.GetAsync(_music.Id);
            if (saved.OwnerId != _ownerId)
            {
                // 合并／拆分后旧的共享草稿不能再把输入传播到另一个所有者。
                var original = _draft;
                original.PropertyChanged -= DraftChanged;
                _draft = new LyricsEditDraft
                {
                    SavedDocument = original.SavedDocument, Revision = original.Revision, OwnerId = original.OwnerId,
                    Loaded = original.Loaded, OriginalText = original.OriginalText, TranslationText = original.TranslationText,
                    PronunciationText = original.PronunciationText, Version = original.Version
                };
                _draft.PropertyChanged += DraftChanged;
            }
            if (saved.Document == _savedDocument) { _revision = saved.Revision; _ownerId = saved.OwnerId; }
            else Error = ToolUtils.GetString("LyricsEditConflict");
            return;
        }
        _draft.PropertyChanged -= DraftChanged;
        _draft = new();
        _draft.PropertyChanged += DraftChanged;
        await LoadAsync();
    }

    public Task ReadEmbeddedAsync() => RunAsync(async token =>
    {
        long draft = _draftVersion;
        string path = _music.Path;
        var result = await Task.Run(async () =>
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var info = await ToolUtils.GetMusicInfo(file);
            string? pronunciation = null;
            string? folder = System.IO.Path.GetDirectoryName(path);
            if (folder is not null)
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                string sidecar = System.IO.Path.Combine(folder, stem + "_Pronunciation.lrc");
                if (System.IO.File.Exists(sidecar))
                {
                    try
                    {
                        string content = await LyricsFilePolicy.ReadAsync(sidecar, token);
                        pronunciation = _parser.NormalizeTranslation(content, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        pronunciation = null;
                    }
                }
            }
            return (Original: info.Item2, Pronunciation: pronunciation);
        }, token);
        token.ThrowIfCancellationRequested();
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        SetDocument(_parser.Import(result.Original, token: token, pronunciation: result.Pronunciation));
    });

    public async Task<bool> SaveAsync(byte[]? cover = null, bool queueMetadata = false, Music? metadataDraft = null)
    {
        bool saved = false;
        if (!_loaded || IsBusy || _disposed) return false;
        if (metadataDraft is not null && (metadataDraft.Id != _music.Id || metadataDraft.SourceId != _music.SourceId ||
            !string.Equals(metadataDraft.Path, _music.Path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Metadata draft belongs to a different track.");
        await RunAsync(async token =>
        {
            long draft = _draftVersion;
            string original = OriginalText, translation = TranslationText, pronunciation = PronunciationText;
            bool lyricsChanged = original != _savedDocument.Original.Content || translation != (_savedDocument.TranslationLrc ?? "") ||
                pronunciation != (_savedDocument.PronunciationLrc ?? "");
            // Unknown legacy content remains authoritative when only metadata is edited.
            var document = lyricsChanged
                ? await Task.Run(() => _parser.Import(original, translation, token: token, pronunciation: pronunciation), token)
                : _savedDocument;
            token.ThrowIfCancellationRequested();
            if (draft != _draftVersion) throw new InvalidOperationException("LyricsEditConflict");
            if (lyricsChanged)
            {
                if (!string.IsNullOrWhiteSpace(original) && !_parser.HasLyrics(document, token)) throw new FormatException("Invalid original lyrics.");
                if (string.IsNullOrWhiteSpace(original) && !string.IsNullOrWhiteSpace(translation)) throw new FormatException("Translation requires original lyrics.");
            }
            if (_music.Id <= 0)
            {
                if (queueMetadata) throw new InvalidOperationException("External tracks must be imported before queuing tag edits.");
                if (!OneShotLyricsCache.SaveEdited(_music.Path, document, _revision)) throw new InvalidOperationException("LyricsEditConflict");
            }
            else if (queueMetadata)
                await _database.QueueMetadataWriteAsync(metadataDraft ?? _music, cover, document, _revision, token, lyricsChanged: lyricsChanged, expectedOwnerId: _ownerId);
            else if (metadataDraft is not null)
                await _database.SaveDetailsAsync(metadataDraft, document, _revision, token, lyricsChanged: lyricsChanged, expectedOwnerId: _ownerId, cover: cover);
            else if (lyricsChanged && !await _database.Lyrics.SaveAsync(_music.Id, document, _revision, "User", token: token, expectedOwnerId: _ownerId))
                throw new InvalidOperationException("LyricsEditConflict");
            if (lyricsChanged || _music.Id <= 0) _revision++;
            _savedDocument = document;
            LastCommittedMetadata = metadataDraft;
            // A binding notification can arrive after the transaction commits. Treat an
            // unchanged text snapshot as success even if that notification bumped the
            // draft counter; a real edit during the transaction still keeps the window open.
            saved = draft == _draftVersion ||
                 (string.Equals(OriginalText, original, StringComparison.Ordinal) &&
                 string.Equals(TranslationText, translation, StringComparison.Ordinal) &&
                 string.Equals(PronunciationText, pronunciation, StringComparison.Ordinal));
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
        string original = OriginalText, translation = TranslationText, pronunciation = PronunciationText;
        var document = await Task.Run(() => _parser.Import(original, translation, token: token, pronunciation: pronunciation), token);
        string path = await LyricsExporter.SaveFilesAsync(_music.Path, document, token);
        if (!_disposed) Status = ToolUtils.GetString("LyricsFilesSaved") + " " + path;
    });

    private Task TranslateAsync() => RunAsync(async token =>
    {
        if (_llm is null) return;
        long draft = _draftVersion;
        string original = OriginalText;
        if (string.IsNullOrWhiteSpace(original))
        {
            Error = ToolUtils.GetString("LyricsInvalid");
            return;
        }
        LyricsDocument document = await Task.Run(() => _parser.Import(original, null, token), token);
        token.ThrowIfCancellationRequested();
        if (!_parser.HasLyrics(document, token))
        {
            Error = ToolUtils.GetString("LyricsInvalid");
            return;
        }
        Status = ToolUtils.GetString("LlmTranslationInProgress");
        string? translation = await _llm.TranslateManuallyAsync(_music, document, token);
        if (string.IsNullOrWhiteSpace(translation))
        {
            Status = "";
            Error = ToolUtils.GetString("LlmManualTranslationFailed");
            return;
        }
        if (draft != _draftVersion)
        {
            Status = "";
            Error = ToolUtils.GetString("LyricsEditConflict");
            return;
        }
        TranslationText = translation;
        Status = ToolUtils.GetString("LlmManualTranslationCompleted");
    });

    private Task RestoreAsync(string? slot) => RunAsync(async token =>
    {
        long draft = _draftVersion;
        var legacy = await _database.GetDbConnection().FindAsync<MusicLyrics>(_music.Id);
        if (legacy is null) return;
        token.ThrowIfCancellationRequested();
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        // Recovery must expose the source pair even when normalization fails.
        string original = slot == "Krc" ? legacy.Krc ?? "" : legacy.Lyrics ?? "";
        string translation = slot == "Krc" ? legacy.TKrc ?? "" : legacy.TranslatedLyrics ?? "";
        OriginalText = original;
        TranslationText = translation;
        draft = _draftVersion;
        var imported = await Task.Run(() =>
        {
            try { return (Document: (LyricsDocument?)_parser.Import(original, translation, token), Error: ""); }
            catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { return (Document: (LyricsDocument?)null, Error: ex.Message); }
        }, token);
        token.ThrowIfCancellationRequested();
        if (draft != _draftVersion) { Error = ToolUtils.GetString("LyricsEditConflict"); return; }
        if (imported.Document is null)
        {
            Error = ToolUtils.GetString("LyricsInvalid") + " " + imported.Error;
            return;
        }
        SetDocument(imported.Document); // Recovery is a draft until the user saves it.
        if ((!string.IsNullOrWhiteSpace(original) && !_parser.HasLyrics(imported.Document, token)) ||
            (string.IsNullOrWhiteSpace(original) && !string.IsNullOrWhiteSpace(translation)))
            Error = ToolUtils.GetString("LyricsInvalid");
    });

    private void SetDocument(LyricsDocument document)
    {
        OriginalText = document.Original.Content;
        TranslationText = document.TranslationLrc ?? "";
        PronunciationText = document.PronunciationLrc ?? "";
    }

    /// <summary>等待已经接受的真实操作；被守卫拒绝的重复命令不会替换此任务。</summary>
    public Task WhenIdleAsync() => _draft.Operation;

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        _operationTask = RunCoreAsync(action);
        _draft.Operation = _operationTask;
        return _operationTask;
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> action)
    {
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
        catch (OperationCanceledException) { if (!_disposed) Status = ""; }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                Status = "";
                Error = ToolUtils.GetString(ex.Message == "LyricsEditConflict" ? "LyricsEditConflict" : "LyricsInvalid") + " " + ex.Message;
            }
        }
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
        _draft.PropertyChanged -= DraftChanged;
        if (_operationTask.IsCompleted) _closed.Dispose();
    }
}

/// <summary>同一详情中共享歌词的文本及修订；不持有窗口或文件资源。</summary>
internal sealed class LyricsEditDraft : ObservableObject
{
    internal LyricsDocument SavedDocument = LyricsDocument.Empty;
    internal long Revision, Version;
    internal int OwnerId;
    internal bool Loaded;
    internal Task Operation = Task.CompletedTask;
    internal string OriginalText { get => field; set { if (SetProperty(ref field, value)) Version++; } } = "";
    internal string TranslationText { get => field; set { if (SetProperty(ref field, value)) Version++; } } = "";
    internal string PronunciationText { get => field; set { if (SetProperty(ref field, value)) Version++; } } = "";
    internal bool IsBusy { get; set => SetProperty(ref field, value); }
}
