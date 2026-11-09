using CommunityToolkit.Mvvm.Input;
using WinUIMusicPlayer.Model;

namespace Microsoft.UI.Xaml
{
    public enum GridUnitType { Star }
    public readonly record struct GridLength(double Value, GridUnitType GridUnitType);
}
namespace WinUIMusicPlayer.Model
{
    public sealed class Folder
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsExternalImport { get; set; }
    }
    public static partial class AppData
    {
        public static Type CurrentPage { get; set; } = typeof(object);
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static partial class ToolUtils
    {
        public static string GetString(string key) => key == "FolderBrowserSummary" ? "{0} songs" : key;
    }
}
namespace WinUIMusicPlayer.View
{
    public sealed class FolderBrowsePage { }
}
namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        public State.AppState State { get; } = new();
        public List<Music> SongsSource => State.Library.Songs;
        public BulkObservableCollection<Music> FolderSongs { get; } = [];
        public string PageType { get; set; } = "";
        public bool IsBackBtnEnable { get; set; }
    }
    public sealed class MusicBrowseViewModel { }
}
namespace WinUIMusicPlayer.Services
{
    public static class FolderCommands
    {
        public static IRelayCommand<FolderBrowserNode> ToggleFolderCommand { get; }
            = new RelayCommand<FolderBrowserNode>(_ => { });
    }

    public sealed partial class MusicDatabaseService
    {
        public List<Folder> Roots { get; set; } = [];
        public async Task<List<Folder>> GetFolders()
        {
            // SQLite 读取仍跨异步边界，结果在调用方的 UI 上下文继续发布。
            await Task.Yield();
            return new(Roots);
        }
    }
}
