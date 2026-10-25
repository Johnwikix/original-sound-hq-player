using BassPlayerIpc.Shared;
using System.Xml.Linq;

namespace WinUIMusicPlayer
{
    public static class App { public static IServiceProvider Services { get; set; } = null!; }
}
namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public string Title { get; set; } = "Test";
        public string Author { get; set; } = "Artist";
        public string Album { get; set; } = "Album";
        public bool IsRemote { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(180);
    }
    public static class AppData
    {
        public static readonly HashSet<string> UnknownArtists = ["Unknown Artist"];
        public static readonly HashSet<string> UnknownAlbums = ["Unknown Album"];
    }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState { public PlaybackState Playback { get; } = new(); }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class IpcService
    {
        public ProgressSnapshot Progress;
        public bool TryGetProgressSnapshot(out ProgressSnapshot snapshot) { snapshot = Progress; return true; }
    }
    public sealed class PlaybackCoordinator
    {
        public event Action<Model.Music, CancellationToken>? TrackStarted;
        public event Action<Model.Music, ProgressSnapshot>? PlaybackConfirmed;
        public void Select(Model.Music music) => TrackStarted?.Invoke(music, default);
        public void Confirm(Model.Music music, ProgressSnapshot snapshot) => PlaybackConfirmed?.Invoke(music, snapshot);
    }
    public sealed class RemotePlaybackService
    {
        public event Action<Model.Music, ProgressSnapshot, bool>? PlaybackStarted;
        public void Confirm(Model.Music music, ProgressSnapshot snapshot, bool resumed = false) => PlaybackStarted?.Invoke(music, snapshot, resumed);
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        private static readonly Dictionary<string, string> Resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Resources.resw"))
            .Root!.Elements("data").Where(element => ((string)element.Attribute("name")!).StartsWith("LastFm", StringComparison.Ordinal)
                || (string)element.Attribute("name")! is "UnknownArtist" or "UnknownAlbum")
            .ToDictionary(element => (string)element.Attribute("name")!, element => (string)element.Element("value")!);
        public static string GetString(string key) => Resources.TryGetValue(key, out string? text) ? text : throw new Exception("Missing localization: " + key);
    }
}
