using System;
using System.ComponentModel;
using Microsoft.UI.Windowing;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>在 UI 线程连接共享窗口状态与 WinUI 原生窗口；显式启动和解绑。</summary>
public sealed class ShellService(AppState state) : IDisposable
{
    private bool _started;
    public void Start()
    {
        if (_started) return;
        _started = true;
        state.Shell.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (state.Lifecycle.Phase != AppPhase.Stopping && e.PropertyName == nameof(ShellState.IsFullScreen))
        {
            var window = App.MainWindow?.AppWindow;
            var desired = state.Shell.IsFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default;
            if (window is not null && window.Presenter.Kind != desired) window.SetPresenter(desired);
        }
    }
    public void UpdateMaximizeState()
    {
        if (App.MainWindow?.AppWindow.Presenter is OverlappedPresenter presenter)
            state.Shell.IsMaximized = presenter.State == OverlappedPresenterState.Maximized;
    }
    public void SyncFullScreenStateFromWindow() => state.Shell.IsFullScreen =
        App.MainWindow?.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    public void Dispose()
    {
        _started = false;
        state.Shell.PropertyChanged -= Changed;
    }
}
