namespace WinUIMusicPlayer
{
    internal static class AppData
    {
        public static string[] UnknownAlbums = [];
        public static string SystemLanguage = "zh-CN";
    }
}

// 与渲染库枚举保持一致；缓存测试无需加载 WinUI/WinRT 解码器。
namespace AnimatedWin2dControls.Impressionist
{
    public enum PaletteAlgorithm : byte { KMeansPP, OctTree }
}

namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        public string Title { get; set; } = "Test Song";
        public string Author { get; set; } = "Test Artist";
        public string Album { get; set; } = "Test Album";
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(180);
    }
}
