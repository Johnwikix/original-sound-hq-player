// 系统输出格式切换工具：读写默认渲染端点的 PKEY_AudioEngine_DeviceFormat
// （与"声音控制面板→属性→高级→输出格式"等效，可触发播放器端点失效场景）。
// 用法：
//   FormatSwitch print              打印当前格式并保存原始 blob 到 %TEMP%\fmt_orig.bin
//   FormatSwitch <采样率>           仅改采样率（位深/声道不变）
//   FormatSwitch restore            恢复保存的原始格式
using System.Runtime.InteropServices;

internal static unsafe class Program
{
    [DllImport("ole32")] static extern int CoInitializeEx(IntPtr r, uint co);
    [DllImport("ole32")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid riid, out IntPtr ppv);
    [DllImport("ole32")] static extern void CoTaskMemFree(IntPtr p);
    [DllImport("ole32")] static extern IntPtr CoTaskMemAlloc(nuint cb);

    static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IidImmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IidImmDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
    static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    static readonly Guid PkeyAudioEngineDeviceFormat = new("F19F064D-082C-4E27-BC73-6882A1BB8E4C");

    static int Main(string[] args)
    {
        CoInitializeEx(IntPtr.Zero, 0 /*MTA*/);
        string save = Path.Combine(Path.GetTempPath(), "fmt_orig.bin");

        var enumClsid = ClsidMmDeviceEnumerator;
        var enumIid = IidImmDeviceEnumerator;
        int hr = CoCreateInstance(ref enumClsid, IntPtr.Zero, 1 /*INPROC*/, ref enumIid, out IntPtr penum);
        if (hr != 0) { Console.WriteLine($"enumerator hr=0x{hr:X8}"); return 1; }

        // GetDefaultAudioEndpoint(eRender=0, eConsole=0) = 槽位 4（无 riid 参数）
        var getDef = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)(*(void***)penum)[4];
        IntPtr pdev = IntPtr.Zero;
        hr = getDef(penum, 0, 0, &pdev);
        if (hr != 0 || pdev == IntPtr.Zero) { Console.WriteLine($"default endpoint hr=0x{hr:X8}"); return 1; }

        // OpenPropertyStore(STGM_READWRITE=2) = 槽位 4（无 riid 参数）
        var openPs = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(*(void***)pdev)[4];
        IntPtr pstore = IntPtr.Zero;
        hr = openPs(pdev, 2, &pstore);
        if (hr != 0 || pstore == IntPtr.Zero) { Console.WriteLine($"OpenPropertyStore hr=0x{hr:X8}"); return 1; }
        var getValue = (delegate* unmanaged[Stdcall]<IntPtr, void*, void*, int>)(*(void***)pstore)[5];
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, void*, void*, int>)(*(void***)pstore)[6];
        var commit = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)pstore)[7];

        Guid keyFmt = PkeyAudioEngineDeviceFormat;
        byte* key = stackalloc byte[20]; // PROPERTYKEY = {GUID fmtid; DWORD pid}，pid=0
        *(Guid*)key = keyFmt;
        *(uint*)(key + 16) = 0;
        var pv = stackalloc byte[32];
        for (int i = 0; i < 32; i++) pv[i] = 0;
        hr = getValue(pstore, key, pv);
        if (hr != 0) { Console.WriteLine($"GetValue hr=0x{hr:X8}"); return 1; }
        ushort vt = *(ushort*)pv;
        if (vt != 65 /*VT_BLOB*/) { Console.WriteLine($"vt={vt} 非 VT_BLOB"); return 1; }
        uint cb = *(uint*)(pv + 8);
        byte* blob = *(byte**)(pv + 16);
        if (cb < 16) { Console.WriteLine($"blob cb={cb} 过小"); return 1; }
        int rate = *(int*)(blob + 4);
        ushort ch = *(ushort*)(blob + 2), bits = *(ushort*)(blob + 14);
        Console.WriteLine($"当前默认端点格式: {rate}Hz {bits}bit {ch}ch (blob {cb}B)");

        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "print";
        if (cmd == "print")
        {
            File.WriteAllBytes(save, new Span<byte>(blob, (int)cb).ToArray());
            Console.WriteLine($"原始 blob 已保存: {save}");
            return 0;
        }

        byte[] data;
        if (cmd == "restore")
        {
            if (!File.Exists(save)) { Console.WriteLine("没有保存过原始格式"); return 1; }
            data = File.ReadAllBytes(save);
            Console.WriteLine($"恢复原始格式: {BitConverter.ToInt32(data, 4)}Hz");
        }
        else
        {
            if (!int.TryParse(args[0], out int newRate)) { Console.WriteLine("用法: print | <采样率> | restore"); return 1; }
            data = new Span<byte>(blob, (int)cb).ToArray();
            fixed (byte* p = data.AsSpan(4))
                *(int*)p = newRate;
            Console.WriteLine($"切换采样率: {rate} -> {newRate}");
        }

        // SetValue：VT_BLOB PROPVARIANT，blob 内存由属性存储复制
        fixed (byte* pkey2 = data)
        {
            var pvSet = stackalloc byte[32];
            for (int i = 0; i < 32; i++) pvSet[i] = 0;
            *(ushort*)pvSet = 65;                       // VT_BLOB
            *(uint*)(pvSet + 8) = (uint)data.Length;    // cbSize
            *(byte**)(pvSet + 16) = pkey2;              // pBlobData
            hr = setValue(pstore, key, pvSet);
            Console.WriteLine($"SetValue hr=0x{hr:X8} {(hr == 0 ? "OK" : "FAIL")}");
            if (hr == 0) { hr = commit(pstore); Console.WriteLine($"Commit hr=0x{hr:X8}"); }
            return hr == 0 ? 0 : 1;
        }
    }
}
