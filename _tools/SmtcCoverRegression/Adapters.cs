using System.Windows.Input;

namespace WinUIMusicPlayer
{
    internal static class App { internal static TestWindow MainWindow { get; } = new(); }
    internal sealed class TestWindow { internal TestDispatcher DispatcherQueue { get; } = new(); }
    internal sealed class TestDispatcher
    {
        internal bool TryEnqueue(Action action) { action(); return true; }
    }
}
namespace WinUIMusicPlayer.Model
{
    internal sealed class Music
    {
        public string Path { get; init; } = "";
        public string ImageHash { get; set; } = "";
        public string CoverOverridePath { get; set; } = "";
        public long CoverAudioWriteTicks { get; set; }
        public string CoverSourceStamp { get; set; } = "";
        public bool IsRemote => false;
    }
    internal static class AppData { internal static bool IsPlaying => false; }
}
namespace WinUIMusicPlayer.ViewModel { internal sealed class NamespaceMarker; }
namespace WinUIMusicPlayer.Services
{
    public sealed class PlaybackCommands
    {
        public ICommand ToggleCommand { get; } = new TestCommand();
        public ICommand PlayCommand { get; } = new TestCommand();
        public ICommand PauseCommand { get; } = new TestCommand();
        public ICommand NextCommand { get; } = new TestCommand();
        public ICommand PreviousCommand { get; } = new TestCommand();
        public ICommand SeekCommand { get; } = new TestCommand();
    }
    internal sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
