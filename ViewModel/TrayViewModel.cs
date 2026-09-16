using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using Microsoft.UI.Windowing;
using WinUIEx;
using WinUIMusicPlayer.DesktopLyrics;
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

    public TrayViewModel(AppLifecycle lifecycle, AppViewModel state, DesktopLyricsViewModel lyrics, PlaybackCommands playback)
    {
        _lifecycle = lifecycle; State = state; DesktopLyrics = lyrics; Playback = playback;
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new AsyncRelayCommand(App.Current_Exit);
        OpenSettingsCommand = ReadyCommand(() => { ShowWindow(); App.Services.GetRequiredService<MainPage>().NavigateToSettingsPage(); });
        ShowPlayingDetailCommand = ReadyCommand(() => { ShowWindow(); App.Services.GetRequiredService<MainPage>().NavigateToPlayingDetailPage(); });
        ToggleDesktopLyricsCommand = ReadyCommand(() => lyrics.IsEnabled = !lyrics.IsEnabled);
        ToggleDesktopLyricsKaraokeCommand = ReadyCommand(() => lyrics.IsKaraokeEnabled = !lyrics.IsKaraokeEnabled);
        ToggleDesktopLyricsLockCommand = ReadyCommand(() => lyrics.IsLocked = !lyrics.IsLocked);
        ResetDesktopLyricsBoundsCommand = ReadyCommand(DesktopLyricsManager.ResetWindowBounds);
        lifecycle.Changed += LifecycleChanged;
    }
    private RelayCommand ReadyCommand(Action action) => new(() => { if (IsReady) action(); }, () => IsReady);
    private static void ShowWindow()
    {
        var window = App.MainWindow;
        if (window is null) return;
        window.Show();
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        window.Activate();
        window.SetForegroundWindow();
    }
    private void LifecycleChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsReady));
        OpenSettingsCommand.NotifyCanExecuteChanged(); ShowPlayingDetailCommand.NotifyCanExecuteChanged();
        ToggleDesktopLyricsCommand.NotifyCanExecuteChanged(); ToggleDesktopLyricsKaraokeCommand.NotifyCanExecuteChanged();
        ToggleDesktopLyricsLockCommand.NotifyCanExecuteChanged(); ResetDesktopLyricsBoundsCommand.NotifyCanExecuteChanged();
    }
    public void Dispose() => _lifecycle.Changed -= LifecycleChanged;
}
