namespace AudioPlayer.Diagnostics;

/// <summary>
/// 诊断转储（AP_DUMP=路径 启用）：把即将提交给设备的原始字节追加写入文件（上限 64MB，
/// 可用 AP_DUMP_MAX 覆盖）。仅用于硬件回归的位流/格式验证；未设置环境变量时零开销。
/// </summary>
internal static unsafe class BufferDump
{
    private static string? _path = Environment.GetEnvironmentVariable("AP_DUMP");
    private static readonly long _maxBytes =
        long.TryParse(Environment.GetEnvironmentVariable("AP_DUMP_MAX"), out long m) && m > 0 ? m : 64 << 20;
    private static FileStream? _fs;
    private static long _written;
    private static readonly object _gate = new();

    public static bool Enabled => _path != null;

    public static void Write(byte* data, int length)
    {
        if (_path == null || length <= 0) return;
        lock (_gate)
        {
            if (_written >= _maxBytes) return;
            if (_fs == null)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
                }
                catch { _path = null; return; }
            }
            try
            {
                _fs.Write(new ReadOnlySpan<byte>(data, length));
                _written += length;
            }
            catch { }
        }
    }
}
