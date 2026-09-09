using System.Runtime.InteropServices;

namespace AudioPlayer.Interop;

/// <summary>裸 Win32 P/Invoke（事件/线程优先级/COM/注册表/窗口），全部 AOT 安全。</summary>
internal static partial class Win32
{
    // ─────────────── 内核对象 ───────────────

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string? lpName);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial uint WaitForMultipleObjects(uint nCount, ReadOnlySpan<IntPtr> lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, int dwMilliseconds);

    // WNDCLASSW 含字符串成员，源生成封送不支持 → 用经典 DllImport（AOT 安全）
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32", SetLastError = true)]
    public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial int GetLastError();

    [LibraryImport("kernel32")]
    public static partial IntPtr GetModuleHandleW(IntPtr lpModuleName);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32")]
    public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32")]
    public static partial void PostQuitMessage(int nExitCode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // ─────────────── COM ───────────────

    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint COINIT_MULTITHREADED = 0x0;
    public const int CLSCTX_ALL = 23;

    [LibraryImport("ole32", SetLastError = true)]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32", SetLastError = true)]
    public static partial int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    [LibraryImport("ole32")]
    public static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport("ole32")]
    public static partial int PropVariantClear(IntPtr pvar);

    // ─────────────── 实时调度（MMCSS）───────────────

    [LibraryImport("avrt", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

    [LibraryImport("winmm")]
    public static partial int timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm")]
    public static partial int timeEndPeriod(uint uPeriod);

    // ─────────────── ASIO 驱动注册表枚举 ───────────────

    // 校验结果按 CLSID 缓存整个进程生命周期：设备列表（IPC 端缓存）与 AsioOutput.Start
    // 的索引→CLSID 映射必须来自同一份过滤结果，否则 UI 选中的序号会指到别的驱动
    private static readonly Dictionary<Guid, bool> AsioDriverUsable = new();
    private static readonly object AsioDriverUsableGate = new();

    /// <summary>
    /// HKLM\SOFTWARE\ASIO\{name}\CLSID。ASIO 驱动以 COM 组件形式注册。
    /// 只返回本进程真正能加载的驱动：驱动卸载后 SOFTWARE\ASIO 项经常残留（卸载程序不清理），
    /// 其 CLSID 在 COM 注册表已不存在或 DLL 已删除；32 位驱动只在 WOW6432Node 注册，
    /// 64 位进程 CoCreateInstance 必失败。bassasio/ASIO SDK 的 getDriverNames 都是裸读注册表，
    /// 所以幽灵驱动会一路透传到 UI——这里在枚举阶段就剔除，且不执行任何驱动代码（只读 PE 头）。
    /// </summary>
    public static List<(string Name, Guid Clsid)> EnumerateAsioDrivers()
    {
        var result = new List<(string, Guid)>();
        using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\\ASIO");
        if (baseKey == null) return result;
        foreach (var name in baseKey.GetSubKeyNames())
        {
            using var driverKey = baseKey.OpenSubKey(name);
            var clsidText = driverKey?.GetValue("CLSID") as string;
            if (clsidText == null || !Guid.TryParse(clsidText, out var clsid)) continue;

            var display = driverKey?.GetValue("Description") as string;
            string shown = string.IsNullOrEmpty(display) ? name : display!;
            if (!IsAsioDriverUsable(clsid, shown)) continue;
            result.Add((shown, clsid));
        }
        return result;
    }

    private static bool IsAsioDriverUsable(Guid clsid, string name)
    {
        lock (AsioDriverUsableGate)
        {
            if (AsioDriverUsable.TryGetValue(clsid, out bool cached)) return cached;
        }
        bool usable = ProbeAsioDriver(clsid, name);
        lock (AsioDriverUsableGate) AsioDriverUsable[clsid] = usable;
        return usable;
    }

    private static bool ProbeAsioDriver(Guid clsid, string name)
    {
        string? dll = ReadInprocServer32(clsid);
        if (dll == null)
        {
            Console.WriteLine($"[asio] skip \"{name}\": CLSID {clsid:B} 无 64 位 InprocServer32 注册（驱动已卸载或仅 32 位）");
            return false;
        }
        string path = Environment.ExpandEnvironmentVariables(dll.Trim().Trim('"'));
        if (!File.Exists(path))
        {
            Console.WriteLine($"[asio] skip \"{name}\": DLL 不存在 {path}");
            return false;
        }
        if (!PeImageMatchesProcess(path, out string why))
        {
            Console.WriteLine($"[asio] skip \"{name}\": {why} {path}");
            return false;
        }
        return true;
    }

    /// <summary>读取 PE 头判断映像架构是否与本进程一致（不加载、不执行 DllMain）。</summary>
    private static bool PeImageMatchesProcess(string path, out string reason)
    {
        reason = "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[0x40];
            if (fs.Read(head) < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z') { reason = "不是 PE 映像"; return false; }
            int lfanew = BitConverter.ToInt32(head[0x3C..]);
            if (lfanew <= 0 || lfanew > 4 * 1024 * 1024) { reason = "PE 头偏移非法"; return false; }
            fs.Position = lfanew;
            Span<byte> nt = stackalloc byte[6];
            if (fs.Read(nt) < 6 || nt[0] != (byte)'P' || nt[1] != (byte)'E' || nt[2] != 0 || nt[3] != 0) { reason = "PE 签名无效"; return false; }
            ushort machine = BitConverter.ToUInt16(nt[4..]);
            ushort expected = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => 0x8664,
                Architecture.Arm64 => 0xAA64,
                Architecture.X86 => 0x014C,
                _ => 0,
            };
            if (expected != 0 && machine != expected)
            {
                reason = $"架构不匹配（映像 0x{machine:X4}，进程 {RuntimeInformation.ProcessArchitecture}）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = $"读取失败 {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>64 位视图的 InprocServer32（HKLM 优先，其次 HKCU 每用户注册）。</summary>
    public static string? ReadInprocServer32(Guid clsid)
    {
        return ReadInprocServer32(Microsoft.Win32.RegistryHive.LocalMachine, clsid)
            ?? ReadInprocServer32(Microsoft.Win32.RegistryHive.CurrentUser, clsid);
    }

    private static string? ReadInprocServer32(Microsoft.Win32.RegistryHive hive, Guid clsid)
    {
        try
        {
            using var base64 = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
            using var key = base64.OpenSubKey(
                "SOFTWARE\\Classes\\CLSID\\" + clsid.ToString("B") + "\\InprocServer32");
            var dll = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(dll) ? null : dll;
        }
        catch { return null; }
    }
}
