using CommunityToolkit.Mvvm.ComponentModel;
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key switch
        {
            "LyricsSourceFile" => "歌词文件",
            "LyricsSourceDatabase" => "数据库",
            "LyricsPriorityFirst" => "第 1 优先级",
            "LyricsPrioritySecond" => "第 2 优先级",
            "LyricsPriorityThird" => "第 3 优先级",
            "LyricsPriorityFourth" => "第 4 优先级",
            _ => key
        };
        public static Task<(Model.Music?, string)> GetMusicInfo(Windows.Storage.StorageFile file) => throw new NotSupportedException();
    }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState { public TestPreferences Preferences { get; } = new(); }
    public sealed class TestPreferences : ObservableObject
    {
        public string LocalLyricsFormatOrder { get => field; set => SetProperty(ref field, Services.Lyrics.LyricsFilePolicy.NormalizeOrder(value)); } = "krc,qrc,lrc,ttml";
        public bool PreferDatabaseLyrics { get; set => SetProperty(ref field, value); }
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel { public State.AppState State { get; } = new(); }
}
namespace WinUIMusicPlayer.DesktopLyrics { public sealed class DesktopLyricsViewModel { } }
