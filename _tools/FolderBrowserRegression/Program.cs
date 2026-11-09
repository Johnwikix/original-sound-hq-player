using CommunityToolkit.Mvvm.Input;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
Music Song(string path, int source = 0, string display = "") => new() { FolderPath = path, SourceId = source, LastLevelFolderPath = display };
var songs = new List<Music>
{
    Song(@"D:\Music"), Song(@"D:\Music\Artist\Album"), Song(@"d:/music/artist/album/"),
    Song(@"E:\Music\Artist\Album"), Song(@"\\server\share\Library\Album"),
    Song("webdav://1/Library/Album/", 1, "Server / Library/Album/"),
    Song("webdav://1/Library/album/", 1, "Server / Library/album/"),
    Song("webdav://2/Library/Album/", 2, "Server / Library/Album/"),
    Song("webdav://1/Library/Disc%2FOne/", 1, "Server / Library/Disc/One/")
};
var folders = new List<Folder>
{
    new() { Path = @"D:\Music\Artist", Name = "Artist" },
    new() { Path = @"D:\Music", Name = "My library" },
    new() { Path = @"E:\Music", Name = "Another library" },
    new() { Path = @"D:\Empty", Name = "Empty" },
    new() { Path = "external://imports", Name = "Imports", IsExternalImport = true }
};
var index = new FolderBrowserIndex();
index.Rebuild(songs, folders, -1);
FolderBrowserNode Find(int id, string path) => index.Find(new(id, FolderLocation.Normalize(path, id), "")) ?? throw new Exception("Missing: " + path);
var local = Find(0, @"D:\Music");
Check(local.Parent is null && local.Name == "My library", "Configured root keeps friendly name");
Check(local.SongCount == 3 && local.DirectSongCount == 1, "Direct songs and descendant counts stay separate");
Check(Find(0, @"D:\Music\Artist").Parent == local, "Overlapping scan roots do not duplicate branches");
var album = Find(0, @"D:\Music\Artist\Album");
Check(album.DirectSongCount == 2 && album.Parent?.Parent == local, "Local case and separators resolve to one directory");
Check(!album.Location.Matches(songs[3]), "Identically named directories on different drives stay separate");
Check(album.Location.Matches(songs[2]), "Projection uses the same local normalization");
Check(Find(0, @"\\server\share\Library\Album").Parent?.Parent?.Path == @"\\server\share", "UNC share terminates ancestry");
Check(Find(0, @"D:\Empty").SongCount == 0, "Empty scan roots remain visible");
Check(index.Roots.All(n => !n.Path.StartsWith("external:")), "Virtual import sentinel is never treated as a physical path");
Check(Find(1, "webdav://1/Library/Album").DirectSongCount == 1, "Remote directory keeps trailing-slash normalization");
Check(Find(1, "webdav://1/Library/album") != Find(1, "webdav://1/Library/Album"), "Remote path case remains distinct");
Check(!Find(1, "webdav://1/Library/Album").Location.Matches(songs[7]), "Remote sources with the same name remain distinct");
Check(Find(1, "webdav://1/Library/Disc%2FOne").Parent?.Path == "webdav://1/Library", "Encoded slash never creates a fake ancestor");
Check(Find(1, "webdav://1").Name == "Server", "Remote root has the source display name");
Check(!local.IsExpanded && local.HasChildren, "Collapsed branches keep their hierarchy without UI containers");
Check(local.Children.All(n => n.Parent == local && n.Depth == local.Depth + 1), "Folder rows derive depth from their stable parent links");
index.Rebuild(songs, folders, -2);
Check(index.Roots.Count == 2 && index.Roots.All(n => n.Location.SourceId != 0), "Remote filter excludes local scan roots");
index.Rebuild(songs, folders, 2);
Check(index.Roots.Count == 1 && index.Roots[0].SongCount == 1, "Specific source scopes both tree and counts");
index.Rebuild(songs, folders, 0);
Check(index.Roots.All(n => n.Location.SourceId == 0), "Local filter excludes remote roots");
index.Rebuild([], [], -1);
Check(index.Roots.Count == 0 && index.Find(album.Location) is null, "Library removal drops old directory references");
Check(StartupEntry.Normalize("Folders") == "Folders" && StartupEntry.Normalize("unknown") == "Home", "Startup supports independent folder navigation");
Console.WriteLine($"PASS: {checks} folder browser checks");

namespace WinUIMusicPlayer.Services
{
    public static class FolderCommands
    {
        public static IRelayCommand<FolderBrowserNode> ToggleFolderCommand { get; }
            = new RelayCommand<FolderBrowserNode>(_ => { });
    }
}
