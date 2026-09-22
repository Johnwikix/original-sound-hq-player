using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public sealed record MusicSourceChoice(int Id, string Name);

/// <summary>来源管理与缓存设置的 UI 状态；服务负责后台工作和停止。</summary>
public partial class WebDavSourcesViewModel : ObservableObject
{
    private readonly MusicDatabaseService _database;
    private readonly WebDavLibraryService _library;
    private readonly RemotePlaybackService _playback;
    private readonly RemoteAudioCache _cache;
    private readonly AppViewModel _app;
    private readonly LibraryQueries _queries;
    private bool _loaded, _loading, _stopped;
    private Task _save = Task.CompletedTask;
    private readonly System.Threading.SemaphoreSlim _loadGate = new(1, 1);
    public ObservableCollection<WebDavSourceItem> Sources { get; } = [];
    public ObservableCollection<MusicSourceChoice> Choices { get; } = [];
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public bool CacheEnabled { get; set { if (SetProperty(ref field, value) && !_loading) SaveCache(); } }
    public double CacheLimitGiB { get; set { if (SetProperty(ref field, value) && !_loading) SaveCache(); } } = 10;
    public string CacheDirectory { get; set { if (SetProperty(ref field, value) && !_loading) SaveCache(); } } = "";
    public string CacheSize { get; private set => SetProperty(ref field, value); } = "";
    public MusicSourceChoice? SelectedSource
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || value is null) return;
            _app.State.Browse.SourceFilterId = value.Id;
            _queries.SetSourceFilter(value.Id);
            _app.RefreshDataSource();
        }
    }
    public WebDavSourcesViewModel(MusicDatabaseService database, WebDavLibraryService library, RemotePlaybackService playback,
        RemoteAudioCache cache, AppViewModel app, LibraryQueries queries)
    {
        _database = database; _library = library; _playback = playback; _cache = cache; _app = app; _queries = queries;
        Choices.Add(new(-1, ToolUtils.GetString("WebDavAllSources")));
        Choices.Add(new(0, ToolUtils.GetString("WebDavLocalSources")));
        Choices.Add(new(-2, "WebDAV"));
        SelectedSource = Choices[0];
        library.StatusChanged += OnStatus;
        library.SourcesChanged += OnSourcesChanged;
    }
    public async Task LoadAsync()
    {
        if (_stopped) return;
        await _loadGate.WaitAsync();
        try
        {
            if (_stopped) return;
            var sources = await _database.GetWebDavSourcesAsync();
            if (_stopped) return;
            Sources.Clear();
            int selected = SelectedSource?.Id ?? -1;
            while (Choices.Count > 3) Choices.RemoveAt(Choices.Count - 1);
            foreach (var source in sources)
            {
                var item = new WebDavSourceItem(source, this);
                item.Update(_library.GetStatus(source.Id));
                Sources.Add(item);
                Choices.Add(new(source.Id, source.Name));
            }
            foreach (var choice in Choices) if (choice.Id == selected) { SelectedSource = choice; break; }
            if (SelectedSource is not null && !Choices.Contains(SelectedSource)) SelectedSource = Choices[0];
            if (!_loaded)
            {
                var settings = await _database.GetWebDavCacheSettingsAsync();
                _loading = true;
                CacheEnabled = settings.Enabled; CacheLimitGiB = settings.LimitGiB; CacheDirectory = settings.Directory;
                _loading = false;
                _loaded = true;
            }
            await UpdateCacheSizeAsync();
        }
        catch { Status = ToolUtils.GetString("WebDavErrorConnectionFailed"); }
        finally { _loadGate.Release(); }
    }
    private void OnSourcesChanged() { if (!_stopped) _ = LoadAsync(); }
    private void OnStatus(WebDavScanStatus status)
    {
        if (_stopped) return;
        foreach (var item in Sources) if (item.Source.Id == status.SourceId) { item.Update(status); break; }
    }
    internal Task ScanAsync(WebDavSource source) => _library.ScanAsync(source);
    internal Task PauseAsync(WebDavSource source) => _library.CancelScanAsync(source.Id);
    internal async Task RemoveAsync(WebDavSource source)
    {
        try
        {
            if (!await DialogHelper.ShowConfirmAsync(App.MainWindow.Content.XamlRoot, "WebDavRemoveConfirm")) return;
            if (_app.CurrentPlayingMusic?.SourceId == source.Id) await _playback.StopAsync();
            await _library.RemoveSourceAsync(source);
        }
        catch { Status = ToolUtils.GetString("WebDavErrorConnectionFailed"); }
    }
    public void ShowSongs(WebDavSource source)
    {
        foreach (var choice in Choices) if (choice.Id == source.Id) { SelectedSource = choice; break; }
    }
    private void SaveCache()
    {
        if (_stopped || !_loaded) return;
        var snapshot = new WebDavCacheSettings { Enabled = CacheEnabled, Directory = CacheDirectory,
            LimitGiB = double.IsFinite(CacheLimitGiB) ? Math.Clamp((int)CacheLimitGiB, 1, 1024) : 10 };
        _save = SaveAfterAsync(_save, snapshot);
    }
    private async Task SaveAfterAsync(Task previous, WebDavCacheSettings settings)
    {
        await previous;
        try { await _library.ApplyCacheSettingsAsync(settings); await UpdateCacheSizeAsync(); }
        catch { Status = ToolUtils.GetString("WebDavCacheUnavailable"); }
    }
    private async Task UpdateCacheSizeAsync()
    {
        long size = await Task.Run(_cache.GetSize);
        if (!_stopped) CacheSize = $"{size / (1024d * 1024 * 1024):F2} GiB";
    }
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try { await Task.Run(_cache.Clear); await UpdateCacheSizeAsync(); }
        catch { Status = ToolUtils.GetString("WebDavCacheUnavailable"); }
    }
    public async Task StopAsync()
    {
        _stopped = true;
        _library.StatusChanged -= OnStatus;
        _library.SourcesChanged -= OnSourcesChanged;
        await _save;
        await _loadGate.WaitAsync();
        _loadGate.Release();
    }
}

public partial class WebDavSourceItem : ObservableObject
{
    public WebDavSource Source { get; }
    public string Name => Source.Name;
    public string Address => Source.BaseUri;
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    private bool _busy;
    public WebDavSourceItem(WebDavSource source, WebDavSourcesViewModel owner)
    {
        Source = source;
        ScanCommand = new AsyncRelayCommand(() => owner.ScanAsync(source), () => !_busy && source.Enabled);
        PauseCommand = new AsyncRelayCommand(() => owner.PauseAsync(source), () => _busy);
        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveAsync(source));
    }
    public void Update(WebDavScanStatus? status)
    {
        _busy = status?.Phase is "Scanning" or "Metadata";
        Status = status is null ? ToolUtils.GetString("WebDavReady") : string.Format(ToolUtils.GetString("WebDavScanSummary"),
            ToolUtils.GetString("WebDav" + status.Phase), status.Found, status.Tagged);
        if (status?.Error is not null) Status += " · " + WebDavText.Error(status.Error);
        ScanCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }
}
