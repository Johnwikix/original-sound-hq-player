using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Utils;

/// <summary>启动前也可使用的文件类型表，不触发 ToolUtils 的日志/Host 初始化。</summary>
internal static class MusicFileTypes
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".oga", ".aiff", ".aif", ".m4a", ".dsf", ".dff", ".ape", ".opus", ".wv"
    };

    internal static bool CanOpen(string extension) => Contains(extension) || extension.Equals(".cue", StringComparison.OrdinalIgnoreCase);

    internal static bool Contains(string extension) => Extensions.Contains(extension);
}
