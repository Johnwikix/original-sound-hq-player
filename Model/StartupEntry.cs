namespace WinUIMusicPlayer.Model;

/// <summary>解析持久化启动入口；有效旧值保持原样，未知值回退到首页。</summary>
public static class StartupEntry
{
    /// <summary>返回支持的入口 Tag。</summary>
    public static string Normalize(string? value) => value is "Home" or "AddFolder" or "MusicBrowse" or "PlayLists" or "Stats" ? value : "Home";
}
