using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel;

public sealed class TrayViewModel : ObservableObject, IDisposable
{
    private readonly AppLifecycle _lifecycle;
    public AppViewModel State { get; }
    public DesktopLyricsViewModel DesktopLyrics { get; }
    public PlaybackCommands Playback { get; }
    public bool IsReady => _lifecycle.IsReady;
    public IRelayCommand ShowWindowCommand { get; }
    public IAsyncRelayCommand ExitCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand ShowPlayingDetailCommand { get; }
    public IRelayCommand ToggleDesktopLyricsCommand { get; }
    public IRelayCommand ToggleDesktopLyricsKaraokeCommand { get; }
    public IRelayCommand ToggleDesktopLyricsLockCommand { get; }
    public IRelayCommand ResetDesktopLyricsBoundsCommand { get; }
    public IRelayCommand<string> SetDesktopLyricsModeCommand { get; }

    public TrayViewModel(AppLifecycle lifecycle, AppViewModel state, DesktopLyricsViewModel lyrics, PlaybackCommands playback, ShellService shell)
    {
        _lifecycle = lifecycle; State = state; DesktopLyrics = lyrics; Playback = playback;
        ShowWindowCommand = new RelayCommand(shell.ShowMainWindow);
        ExitCommand = new AsyncRelayCommand(App.Current_Exit);
        OpenSettingsCommand = ReadyCommand(() => { shell.ShowMainWindow(); App.Services.GetRequiredService<MainPage>().NavigateToSettingsPage(); });
        ShowPlayingDetailCommand = ReadyCommand(() => { shell.ShowMainWindow(); App.Services.GetRequiredService<MainPage>().NavigateToPlayingDetailPage(); });
        ToggleDesktopLyricsCommand = ReadyCommand(() => lyrics.IsEnabled = !lyrics.IsEnabled);
        ToggleDesktopLyricsKaraokeCommand = ReadyCommand(() => lyrics.IsKaraokeEnabled = !lyrics.IsKaraokeEnabled);
        ToggleDesktopLyricsLockCommand = ReadyCommand(() => lyrics.IsLocked = !lyrics.IsLocked);
        ResetDesktopLyricsBoundsCommand = ReadyCommand(DesktopLyricsManager.ResetWindowBounds);
        SetDesktopLyricsModeCommand = new RelayCommand<string>(SetDesktopLyricsMode, mode => IsReady);
        lifecycle.Changed += LifecycleChanged;
    }

    private void SetDesktopLyricsMode(string? mode)
    {
        if (!IsReady || !Enum.TryParse(mode, ignoreCase: true, out DesktopLyricsMode parsed) ||
            parsed is not (DesktopLyricsMode.Floating or DesktopLyricsMode.Taskbar)) return;
        DesktopLyrics.Mode = parsed;
    }
    private RelayCommand ReadyCommand(Action action) => new(() => { if (IsReady) action(); }, () => IsReady);
    private void LifecycleChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsReady));
        OpenSettingsCommand.NotifyCanExecuteChanged(); ShowPlayingDetailCommand.NotifyCanExecuteChanged();
        ToggleDesktopLyricsCommand.NotifyCanExecuteChanged(); ToggleDesktopLyricsKaraokeCommand.NotifyCanExecuteChanged();
        ToggleDesktopLyricsLockCommand.NotifyCanExecuteChanged(); ResetDesktopLyricsBoundsCommand.NotifyCanExecuteChanged();
        SetDesktopLyricsModeCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() => _lifecycle.Changed -= LifecycleChanged;
}
