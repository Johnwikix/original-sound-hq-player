using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>所有交互入口共用命令与就绪判定。执行时才进入现有播放实现。</summary>
public sealed class PlaybackCommands : IDisposable
{
    private readonly AppLifecycle _lifecycle;
    private readonly AppViewModel _state;
    private readonly IServiceProvider _services;
    private bool _toggleInFlight;
    private INotifyCollectionChanged? _listChanges;
    public IAsyncRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IRelayCommand<long> SeekCommand { get; }
    private bool CanPlay => _lifecycle.IsReady && _state.CurrentPlayingMusic is not null;
    private bool CanSwitch => CanPlay && _state.CurrentPlayingList.Count > 0;
    private BassPlayerCommandService Player => _services.GetRequiredService<BassPlayerCommandService>();

    public PlaybackCommands(AppLifecycle lifecycle, AppViewModel state, IServiceProvider services)
    {
        _lifecycle = lifecycle;
        _state = state;
        _services = services;
        ToggleCommand = new AsyncRelayCommand(() => ToggleAsync(null), () => CanPlay);
        PlayCommand = new AsyncRelayCommand(() => ToggleAsync(true), () => CanPlay);
        PauseCommand = new AsyncRelayCommand(() => ToggleAsync(false), () => CanPlay);
        NextCommand = new RelayCommand(Next, () => CanSwitch);
        PreviousCommand = new AsyncRelayCommand(PreviousAsync, () => CanSwitch);
        SeekCommand = new RelayCommand<long>(Seek, _ => CanPlay);
        lifecycle.Changed += Changed;
        state.PropertyChanged += StateChanged;
        ObserveList();
    }
    private async Task ToggleAsync(bool? playing)
    {
        if (!CanPlay || _toggleInFlight || (playing.HasValue && _state.IsPlaying == playing.Value)) return;
        _toggleInFlight = true;
        try { await Player.PlayButton(); }
        finally { _toggleInFlight = false; }
    }
    private void Next() { if (CanSwitch) Player.PlayNextTrack(); }
    private async Task PreviousAsync()
    {
        if (!CanSwitch) return;
        var list = _state.CurrentPlayingList;
        int index = -1;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == _state.CurrentPlayingMusic!.Id) { index = i; break; }
        if (index > 0 || (index == 0 && list.Count > 1))
            await _services.GetRequiredService<MusicBrowseViewModel>().PlayMusic(list[index > 0 ? index - 1 : list.Count - 1]);
    }
    private void Seek(long milliseconds) { if (CanPlay) Player.ChangeWaveChannelTime(Math.Max(0, milliseconds)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.CurrentPlayingList)) ObserveList();
        if (e.PropertyName is nameof(AppViewModel.CurrentPlayingMusic) or nameof(AppViewModel.CurrentPlayingList)) Changed(sender, EventArgs.Empty);
    }
    private void ObserveList()
    {
        if (_listChanges is not null) _listChanges.CollectionChanged -= ListChanged;
        _listChanges = _state.CurrentPlayingList as INotifyCollectionChanged;
        if (_listChanges is not null) _listChanges.CollectionChanged += ListChanged;
    }
    private void ListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed(sender, EventArgs.Empty);
    private void Changed(object? sender, EventArgs e)
    {
        ToggleCommand.NotifyCanExecuteChanged(); PlayCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged(); SeekCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        _lifecycle.Changed -= Changed; _state.PropertyChanged -= StateChanged;
        if (_listChanges is not null) _listChanges.CollectionChanged -= ListChanged;
    }
}
