// 决定性实验：SetDllDirectory 后裸 LoadLibrary 能否命中兄弟目录（应用根）的 dll
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("kernel32", EntryPoint = "SetDllDirectoryW", CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectory(string p);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr LoadLibrary(string name);

    static int Main()
    {
        string appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Console.WriteLine($"exeDir={AppContext.BaseDirectory}");
        Console.WriteLine($"appRoot={appRoot} hasAvcodec={File.Exists(Path.Combine(appRoot, "avcodec-63.dll"))}");
        bool ok = SetDllDirectory(appRoot);
        Console.WriteLine($"SetDllDirectory -> {ok} err={Marshal.GetLastWin32Error()}");
        
        IntPtr h = LoadLibrary("avcodec-63.dll");
        Console.WriteLine($"LoadLibrary(bare) -> 0x{h:X} err={Marshal.GetLastWin32Error()}");
        return h != IntPtr.Zero ? 0 : 1;
    }
}
