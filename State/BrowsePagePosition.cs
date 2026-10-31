namespace WinUIMusicPlayer.State;

/// <summary>会话内的纯值位置快照；不保存控件、数据项、集合或异步任务。</summary>
public sealed class BrowsePagePosition
{
    public bool IsZoomedIn { get; set; } = true;
    public BrowseViewportPosition Items { get; } = new();
    public BrowseViewportPosition Groups { get; } = new();
    public BrowseViewportPosition Details { get; } = new();
}

public sealed class BrowseViewportPosition
{
    public BrowseViewportBookmark? Bookmark { get; set; }
}

public readonly record struct BrowseViewportBookmark(
    string Context, string? AnchorKey, int Index, double X, double Y,
    double HorizontalOffset, double VerticalOffset);

/// <summary>固定五个页签，每个视图只保留最近一次位置，容量不随访问过的分组增长。</summary>
public sealed class LibraryBrowsePositions
{
    public BrowsePagePosition Songs { get; } = new();
    public BrowsePagePosition Favorites { get; } = new();
    public BrowsePagePosition Albums { get; } = new();
    public BrowsePagePosition Artists { get; } = new();
    public BrowsePagePosition Folders { get; } = new();
}
