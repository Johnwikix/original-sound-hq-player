namespace WinUIMusicPlayer.State;

public sealed class AppState
{
    public Services.AppLifecycle Lifecycle { get; } = new();
    public ShellState Shell { get; } = new();
    public ViewModel.ProgressCenter Operations { get; } = new();
}
