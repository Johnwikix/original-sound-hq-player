using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OriginalSound.Plugin;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Plugins;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.ViewModel.Pages;

public sealed partial class PluginPageViewModel(PluginManager plugins, AppState state, MusicDatabaseService database, LyricsLoader loader) : ObservableObject, IDisposable
{
    private CancellationTokenSource _lifetime = new();
    private PluginRoute? _route;
    private Music? _target;
    public ObservableCollection<LyricsCandidate> Results { get; } = [];
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string artist = "";
    [ObservableProperty] private string message = "";
    [ObservableProperty] private LyricsCandidate? selected;
    public string Preview => Selected?.Text ?? "";
    partial void OnSelectedChanged(LyricsCandidate? value) { OnPropertyChanged(nameof(Preview)); ApplyCommand.NotifyCanExecuteChanged(); }

    public void Activate(PluginRoute route)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _route = route;
        Title = route.Title;
        Query = state.Playback.CurrentPlayingMusic?.Title ?? "";
        Artist = state.Playback.CurrentPlayingMusic?.Author ?? "";
        Message = ToolUtils.GetString("PluginSearchHint");
        Results.Clear();
        Selected = null;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_route is null || string.IsNullOrWhiteSpace(Query)) return;
        var ct = _lifetime.Token;
        var route = _route;
        _target = state.Playback.CurrentPlayingMusic;
        Selected = null;
        Results.Clear();
        Message = ToolUtils.GetString("PluginSearching");
        var metadata = _target is null ? new TrackMetadata("", Query, Artist, "", null, AppData.SystemLanguage)
            : LrcService.Metadata(_target) with { Title = Query, Artist = Artist };
        try
        {
            var result = await plugins.InvokeAsync(route.PluginId, new() { Method = "search", Track = metadata }, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var candidate in result.Lyrics.Take(100)) Results.Add(candidate);
            Message = ToolUtils.GetString(result.Result == PluginResult.Found ? "PluginSelectResult" : result.Result == PluginResult.NoResult ? "PluginNoResult" : "PluginRequestFailed");
        }
        catch (OperationCanceledException) { }
    }

    private bool CanApply() => Selected is not null && _target is not null && !_lifetime.IsCancellationRequested;
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Selected is not { } lyric || _target is not { } target || _route is null ||
            !plugins.Routes.Any(x => x.PluginId == _route.PluginId)) return;
        var ct = _lifetime.Token;
        bool words = lyric.Format is "qrc" or "krc";
        if (lyric.Format is not ("lrc" or "qrc" or "krc")) return;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (target.Id > 0)
                await database.SaveLyricsAsync(target.Id, words ? "" : lyric.Text, words ? "" : lyric.Translation,
                    words ? lyric.Text : "", words ? lyric.Translation : "", userSelected: true);
            else
            {
                bool saved = await Task.Run(() => OneShotLyricsCache.Save(target.Path, words ? lyric.Text : "", words ? lyric.Translation : "",
                    words ? "" : lyric.Text, words ? "" : lyric.Translation, userSelected: true), ct);
                if (!saved) throw new System.IO.IOException("Lyrics cache write failed.");
            }
            if (ct.IsCancellationRequested) return;
            if (ReferenceEquals(state.Playback.CurrentPlayingMusic, target)) loader.Load(target, countPlayback: false);
            Message = ToolUtils.GetString("PluginApplied");
        }
        catch (OperationCanceledException) { }
        catch { Message = ToolUtils.GetString("PluginSaveFailed"); }
    }
    public void Dispose() { _lifetime.Cancel(); ApplyCommand.NotifyCanExecuteChanged(); }
}
