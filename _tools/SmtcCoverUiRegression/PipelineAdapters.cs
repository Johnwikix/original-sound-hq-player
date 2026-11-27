using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AnimatedWin2dControls.Impressionist;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;

// 仅替换歌曲获取、设置和命令入口；发布使用真实 DispatcherQueue，
// 封面写入、取色、展示服务、任务屏障和 SMTC 均编译生产源码。
namespace WinUIMusicPlayer.Model
{
    public sealed class Music : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public long CoverAudioWriteTicks { get; set; }
        public string CoverSourceStamp { get; set { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(nameof(CoverSourceStamp))); } } = "";
        public string CoverOverridePath { get; set; } = "";
        public string ImageHash { get; set { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(nameof(ImageHash))); } } = "";
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public string Author => "artist";
        public string Album => "album";
        public string Extension => ".flac";
        public bool IsRemote { get; init; }
        public int SampleRate => 44100;
        public int BitDepth => 16;
        public int BitRate => 1000;
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
    }
    internal static class AppData { internal static bool IsPlaying => false; }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState(AppLifecycle lifecycle)
    {
        public AppLifecycle Lifecycle { get; } = lifecycle;
        public PreferencesState Preferences { get; } = new();
        public PlaybackState Playback { get; } = new();
        public PresentationState Presentation { get; } = new();
    }
    public sealed class PlaybackState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public Model.Music? CurrentPlayingMusic
        {
            get;
            set
            {
                var previous = DisplayedPlaybackMusic;
                if (ReferenceEquals(field, value)) return;
                field = value;
                if (!ReferenceEquals(previous, DisplayedPlaybackMusic))
                    PropertyChanged?.Invoke(this, new(nameof(DisplayedPlaybackMusic)));
            }
        }
        public Model.Music? DisplayedPlaybackMusic => TransitionPresentation?.Music ?? CurrentPlayingMusic;
        public PlaybackTransitionPresentation? TransitionPresentation
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                PropertyChanged?.Invoke(this, new(nameof(TransitionPresentation)));
            }
        }
    }
    public readonly record struct PlaybackTransitionPresentation(Model.Music Music, bool IsCommitted = false);
    public sealed class PreferencesState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsDarkMode
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                PropertyChanged?.Invoke(this, new(nameof(IsDarkMode)));
            }
        }
        public string ThemeType => "Light";
        public string MusicCoverCache => AppContext.BaseDirectory;
        public bool IsAutoCoverEnabled
        {
            get;
            set
            {
                if (field == value) return;
                field = value;
                PropertyChanged?.Invoke(this, new(nameof(IsAutoCoverEnabled)));
            }
        }
        public PaletteAlgorithm PaletteAlgorithm => PaletteAlgorithm.KMeansPP;
        public AnimatedWin2dControls.BackgroundShaderMode BackgroundShader { get; set; }
    }
    public sealed class PresentationState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string LyricPageBackgroundHash { get; set => Publish(ref field, value); } = "";
        public (PaletteResult? Palette, ArtworkPixelData? Artwork) LyricPageBackdrop { get; set => Publish(ref field, value); }

        private void Publish<T>(ref T current, T value, [CallerMemberName] string property = "")
        {
            if (!App.MainWindow.DispatcherQueue.HasThreadAccess)
                throw new Exception("封面状态在非 UI 线程发布");
            if (EqualityComparer<T>.Default.Equals(current, value)) return;
            current = value;
            PropertyChanged?.Invoke(this, new(property));
        }
        public string MusicInfo { get; set; } = "";
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class WebDavLibraryService
    {
        internal Task TrimCoverCacheAsync(string path, CancellationToken token)
            => Task.CompletedTask;
    }
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
namespace WinUIMusicPlayer.Helper
{
    internal static class CoverLoadQueue
    {
        internal const int CoverSize = 150;
        internal static int ThumbnailReads;
        internal static string GetThumbCachePath(string hash, int size)
        {
            Interlocked.Increment(ref ThumbnailReads);
            return Utils.ToolUtils.FindRawCachePath("small");
        }
    }
}
namespace WinUIMusicPlayer.Utils
{
    internal static partial class ToolUtils
    {
        internal static int RawReads, StoreFailures;
        internal static TaskCompletionSource? ReadStarted, ContinueRead;
        internal static Func<bool>? IsRemoteMatchingEnabled;
        internal static bool GetIsLightTheme() => true;
        internal static async Task<byte[]> GetRawImage(Model.Music music, bool manual, CancellationToken token)
        {
            Interlocked.Increment(ref RawReads);
            ReadStarted?.TrySetResult();
            if (ContinueRead is { } gate) await gate.Task.WaitAsync(token);
            if (music.IsRemote && IsRemoteMatchingEnabled?.Invoke() != true) return [];
            if (music.Path.Length == 0) return [];
            byte[] bytes = await File.ReadAllBytesAsync(music.Path, token);
            try { await PlaybackCoverCache.StoreAsync(FindRawCachePath(music.ImageHash), bytes, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Interlocked.Increment(ref StoreFailures); }
            return bytes;
        }
    }
}
