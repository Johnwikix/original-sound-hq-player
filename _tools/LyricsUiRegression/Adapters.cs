using CommunityToolkit.Mvvm.ComponentModel;
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key == "LyricsFilePriorityLabel" ? "歌词文件优先级" : key;
        public static Task<(Model.Music?, string)> GetMusicInfo(Windows.Storage.StorageFile file) => throw new NotSupportedException();
    }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState { public TestPreferences Preferences { get; } = new(); }
    public sealed class TestPreferences : ObservableObject
    {
        public string LocalLyricsFormatOrder { get => field; set => SetProperty(ref field, value); } = "krc,qrc,lrc,ttml";
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel { public State.AppState State { get; } = new(); }
}
namespace WinUIMusicPlayer.DesktopLyrics { public sealed class DesktopLyricsViewModel { } }
