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

    /// <summary>HKLM\SOFTWARE\ASIO\{name}\CLSID。ASIO 驱动以 COM 组件形式注册。</summary>
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
            result.Add((string.IsNullOrEmpty(display) ? name : display!, clsid));
        }
        return result;
    }

    public static string? ReadInprocServer32(Guid clsid)
    {
        try
        {
            using var base64 = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = base64.OpenSubKey(
                "SOFTWARE\\Classes\\CLSID\\" + clsid.ToString("B") + "\\InprocServer32");
            var dll = key?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(dll) ? null : dll;
        }
        catch { return null; }
    }
}
