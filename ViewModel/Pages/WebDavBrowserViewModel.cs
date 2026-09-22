using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public partial class WebDavBrowserViewModel(WebDavSource source, WebDavLibraryService library, WebDavTransport transport,
    MusicDatabaseService database, PlaybackCoordinator playback) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(library.StoppingToken);
    private Task _work = Task.CompletedTask;
    private bool _disposed;
    public ObservableCollection<WebDavEntry> Entries { get; } = [];
    public string Directory { get; private set => SetProperty(ref field, value); } = WebDavTransport.NormalizeRoot(source.BaseUri).AbsolutePath;
    public string Status { get; private set => SetProperty(ref field, value); } = "";
    private bool _busy;
    public Task LoadAsync(string? directory = null) => _busy || _disposed ? _work : _work = LoadCoreAsync(directory);
    private async Task LoadCoreAsync(string? directory)
    {
        if (_busy || _stop.IsCancellationRequested) return;
        _busy = true;
        try
        {
            Directory = directory ?? Directory;
            Entries.Clear();
            Status = ToolUtils.GetString("WebDavConnecting");
            await foreach (var entry in transport.ListAsync(library.Connect(source), Directory, _stop.Token)) Entries.Add(entry);
            Status = ToolUtils.GetString("WebDavBrowserHint");
        }
        catch (OperationCanceledException) { }
        catch { Status = ToolUtils.GetString("WebDavErrorConnectionFailed"); }
        finally { _busy = false; }
    }
    [RelayCommand]
    private Task UpAsync()
    {
        string root = WebDavTransport.NormalizeRoot(source.BaseUri).AbsolutePath;
        if (Directory == root) return Task.CompletedTask;
        string parent = Directory.TrimEnd('/');
        parent = parent[..(parent.LastIndexOf('/') + 1)];
        return LoadAsync(parent.StartsWith(root, StringComparison.Ordinal) ? parent : root);
    }
    public async Task OpenAsync(WebDavEntry entry)
    {
        if (entry.IsDirectory) { await LoadAsync(entry.Href); return; }
        var music = await database.GetRemoteMusicAsync(source.Id, entry.Href);
        if (music is null) { Status = ToolUtils.GetString("WebDavIndexFirst"); return; }
        await playback.PlayAsync(music);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _ = DrainAsync(_work);
    }
    private async Task DrainAsync(Task work)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        finally { _stop.Dispose(); }
    }
}
