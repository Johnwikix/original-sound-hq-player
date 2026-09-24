using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>所有交互入口共用命令与就绪判定。执行时才进入现有播放实现。</summary>
public sealed class PlaybackCommands : IDisposable
{
    private readonly AppLifecycle _lifecycle;
    private readonly AppViewModel _state;
    private readonly IServiceProvider _services;
    private bool _toggleInFlight;
    private bool? _pendingPlaying;
    private volatile bool _disposed;
    private readonly DispatcherQueueHandler _refreshAvailability;
    private INotifyCollectionChanged? _listChanges;
    private Music? _currentMusic;
    public IAsyncRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IRelayCommand<long> SeekCommand { get; }
    // 引擎就绪（IPC 连接 + 首曲推送完成）是播放的前提；IsPlaybackEngineReady 只在置位时包含 Ready，
    // 退出转 Stopping 后不会复位，生命周期守卫须单独保留。
    private bool CanPlay => !_disposed && _lifecycle.IsReady && _state.IsPlaybackEngineReady && _state.CurrentPlayingMusic is not null;
    private bool CanSeek => CanPlay && _state.CurrentPlayingMusic is { IsPlayable: true };
    private bool CanSwitch => !_disposed && _lifecycle.IsReady && _state.IsPlaybackEngineReady && HasCandidateEntry();
    private BassPlayerCommandService Player => _services.GetRequiredService<BassPlayerCommandService>();

    public PlaybackCommands(AppLifecycle lifecycle, AppViewModel state, IServiceProvider services)
    {
        _lifecycle = lifecycle;
        _state = state;
        _services = services;
        _refreshAvailability = RefreshAvailability;
        ToggleCommand = new AsyncRelayCommand(() => ToggleAsync(null), () => CanPlay, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        // 保持显式命令可用，让 Play -> Pause -> Play 的最后一次意图能够覆盖待执行意图。
        PlayCommand = new AsyncRelayCommand(() => ToggleAsync(true), () => CanPlay, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PauseCommand = new AsyncRelayCommand(() => ToggleAsync(false), () => CanPlay, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NextCommand = new RelayCommand(Next, () => CanSwitch);
        PreviousCommand = new AsyncRelayCommand(PreviousAsync, () => CanSwitch, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        SeekCommand = new RelayCommand<long>(Seek, _ => CanSeek);
        lifecycle.Changed += Changed;
        state.PropertyChanged += StateChanged;
        ObserveList();
        ObserveCurrentMusic();
    }
    private async Task ToggleAsync(bool? playing)
    {
        if (!CanPlay) return;
        if (playing == false && _state.State.Playback.PendingSelection is not null)
            _services.GetRequiredService<PlaybackCoordinator>().CancelPendingSelection();
        var remote = _services.GetRequiredService<RemotePlaybackService>();
        if (_state.CurrentPlayingMusic?.IsRemote == true)
        {
            if (remote.NeedsStart)
            {
                if (playing != false) await _services.GetRequiredService<PlaybackCoordinator>().PlayAsync(_state.CurrentPlayingMusic);
                else await remote.SetIntentAsync(false);
                return;
            }
            await remote.SetIntentAsync(playing ?? !remote.WantsPlay);
            return;
        }
        if (_toggleInFlight)
        {
            // 重复 toggle 合并；显式播放/暂停保存最新意图，在后端确认状态后再判断。
            if (playing.HasValue) _pendingPlaying = playing;
            return;
        }
        if (playing.HasValue && _state.IsPlaying == playing.Value) return;
        _toggleInFlight = true;
        try
        {
            do
            {
                _pendingPlaying = null;
                await Player.PlayButton();
                playing = _pendingPlaying;
            }
            while (CanPlay && playing.HasValue && _state.IsPlaying != playing.Value);
        }
        finally
        {
            _pendingPlaying = null;
            _toggleInFlight = false;
        }
    }
    private void Next() { if (CanSwitch) Player.PlayNextTrack(); }
    private async Task PreviousAsync()
    {
        if (!CanSwitch) return;
        var list = _state.CurrentPlayingList;
        var coordinator = _services.GetRequiredService<PlaybackCoordinator>();
        int index = coordinator.GetNavigationIndex();
        if (index < 0) return;
        int previous = FindCandidateIndex(list, index, -1);
        if (previous >= 0) await coordinator.PlayAtAsync(previous, direction: -1);
    }
    private bool HasCandidateEntry()
    {
        foreach (var music in _state.CurrentPlayingList) if (music.IsRemote || music.IsPlayable) return true;
        return false;
    }
    internal static int FindCandidateIndex(IReadOnlyList<Music> list, int current, int direction)
    {
        if (list.Count == 0) return -1;
        int index = current;
        for (int i = 0; i < list.Count; i++)
        {
            index = (index + direction + list.Count) % list.Count;
            // 来源状态只是上次探活的结果；实际可用性由协调器重新确认。
            if ((list[index].IsRemote || list[index].IsPlayable) && index != current) return index;
        }
        return -1;
    }
    private void Seek(long milliseconds) { if (CanSeek) Player.ChangeWaveChannelTime(Math.Max(0, milliseconds)); }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.CurrentPlayingMusic) or nameof(AppViewModel.CurrentPlayingList)
            or nameof(AppViewModel.IsPlaybackEngineReady))
        {
            if (e.PropertyName == nameof(AppViewModel.CurrentPlayingMusic)) ObserveCurrentMusic();
            Changed(sender, EventArgs.Empty);
        }
    }
    private void ObserveCurrentMusic()
    {
        if (ReferenceEquals(_currentMusic, _state.CurrentPlayingMusic)) return;
        if (_currentMusic is not null) _currentMusic.PropertyChanged -= CurrentMusicChanged;
        _currentMusic = _state.CurrentPlayingMusic;
        if (_currentMusic is not null) _currentMusic.PropertyChanged += CurrentMusicChanged;
    }
    private void CurrentMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Music.IsRemoteOffline) or nameof(Music.IsPlayable)) Changed(sender, EventArgs.Empty);
    }
    private void ObserveList()
    {
        if (ReferenceEquals(_listChanges, _state.CurrentPlayingList)) return;
        if (_listChanges is not null) _listChanges.CollectionChanged -= ListChanged;
        _listChanges = _state.CurrentPlayingList as INotifyCollectionChanged;
        if (_listChanges is not null) _listChanges.CollectionChanged += ListChanged;
    }
    private void ListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Changed(sender, EventArgs.Empty);
    private void Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var queue = App.MainWindow.DispatcherQueue;
        if (queue.HasThreadAccess) RefreshAvailability();
        else queue.TryEnqueue(_refreshAvailability);
    }
    private void RefreshAvailability()
    {
        if (_disposed) return;
        ObserveList();
        ToggleCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        SeekCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        _disposed = true;
        _lifecycle.Changed -= Changed;
        _state.PropertyChanged -= StateChanged;
        if (_listChanges is not null) _listChanges.CollectionChanged -= ListChanged;
        if (_currentMusic is not null) _currentMusic.PropertyChanged -= CurrentMusicChanged;
    }
}
