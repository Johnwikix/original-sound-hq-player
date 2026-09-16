namespace WinUIMusicPlayer.Helper;

/// <summary>仅在 UI 线程访问。请求代次与已显示内容分开，取消/失败不提交显示状态。</summary>
internal sealed class CoverLoadState
{
    private int _version;
    private bool _hasSource;
    private string? _displayedHash;
    private bool _usesDefault;
    private bool _isDark;

    public bool Begin(string? hash, bool isDark, out int version)
    {
        version = ++_version; // 即使复用已显示内容，也使先前请求失效（A → B → A）。
        return !_hasSource || (hash ?? "") != _displayedHash || (_usesDefault && isDark != _isDark);
    }

    public bool IsCurrent(int version) => version == _version;

    public void Commit(int version, string? hash, bool usesDefault, bool isDark)
    {
        if (!IsCurrent(version)) return;
        _hasSource = true;
        _displayedHash = hash ?? "";
        _usesDefault = usesDefault;
        _isDark = isDark;
    }

    public void Reset()
    {
        _version++;
        _hasSource = false;
        _displayedHash = null;
    }
}
