namespace WinUIMusicPlayer.Model
{
    /// <summary>
    /// 转换时内联写入输出文件的元数据（FFmpeg 原生标签/封面/歌词），
    /// 使支持的容器免去转换后 ATL 整文件重写。
    /// </summary>
    public sealed record ConversionMetadata
    {
        public required string Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public int TrackNumber { get; init; }
        public int DiscNumber { get; init; }
        public int Year { get; init; }
        /// <summary>封面图片原始字节（JPEG/PNG），null 表示无封面。</summary>
        public byte[]? CoverBytes { get; init; }
        public string CoverMime { get; init; } = "image/jpeg";
        /// <summary>歌词全文（多行），null 表示无歌词。</summary>
        public string? Lyrics { get; init; }
    }
}
