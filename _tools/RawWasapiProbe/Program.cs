using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// 最小 WASAPI 共享模式探针：验证三种 Initialize 方式在本机的成败
// A) WAVEFORMATEXTENSIBLE float32 44.1k（Pack=2）
// B) 纯 WAVEFORMATEX IEEE_FLOAT 44.1k（非扩展）
// C) GetMixFormat 原样回传

internal static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    static readonly Guid ClsidEnum = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IidEnum = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IidClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IeeeFloat = new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    [DllImport("ole32")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr p, uint ctx, ref Guid iid, out IntPtr obj);

    [DllImport("ole32")]
    static extern int CoInitializeEx(IntPtr p, uint coinit);

    static void Main()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        int hr = CoCreateInstance(ref Unsafe.AsRef(in ClsidEnum), IntPtr.Zero, 23, ref Unsafe.AsRef(in IidEnum), out IntPtr en);
        Console.WriteLine($"enumerator hr=0x{hr:X8}");
        if (hr != 0) return;

        void** evtbl = *(void***)en;
        var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)evtbl[4];
        IntPtr dev = IntPtr.Zero;
        hr = getDefault(en, 0, 0, &dev);
        Console.WriteLine($"default endpoint hr=0x{hr:X8} ptr={dev}");
        if (hr != 0 || dev == IntPtr.Zero) return;

        var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, void*, IntPtr*, int>)(*(void***)dev)[3];
        var init = (delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)null!;
        var getMix = (delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEX**, int>)null!;
        var getBufSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)null!;

        IntPtr devLocal = dev;
        IntPtr ActivateClient()
        {
            Guid iid = IidClient;
            IntPtr c = IntPtr.Zero;
            hr = activate(devLocal, &iid, 23, null, &c);
            if (hr != 0) { Console.WriteLine($"activate hr=0x{hr:X8}"); return IntPtr.Zero; }
            void** v = *(void***)c;
            init = (delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)v[3];
            getMix = (delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEX**, int>)v[8];
            getBufSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)v[4];
            return c;
        }

        // A) Extensible float32
        IntPtr a = ActivateClient();
        if (a != IntPtr.Zero)
        {
            var ext = new WAVEFORMATEXTENSIBLE
            {
                Format = new WAVEFORMATEX { wFormatTag = 0xFFFE, nChannels = 2, nSamplesPerSec = 44100, wBitsPerSample = 32, cbSize = 22 },
                wValidBitsPerSample = 32,
                dwChannelMask = 3,
                SubFormat = IeeeFloat,
            };
            ext.Format.nBlockAlign = 8;
            ext.Format.nAvgBytesPerSec = 44100 * 8;
            Console.WriteLine($"sizeof(WAVEFORMATEXTENSIBLE)={sizeof(WAVEFORMATEXTENSIBLE)}");
            hr = init(a, 0, 0x00040000 | 0x00080000, 3000000, 0, &ext.Format, null);
            Console.WriteLine($"A extensible-float hr=0x{hr:X8}");
            if (hr == 0) { uint n = 0; getBufSize(a, &n); Console.WriteLine($"  buffer={n}"); }
        }

        // B) 纯 WAVEFORMATEX float
        IntPtr b = ActivateClient();
        if (b != IntPtr.Zero)
        {
            var fmt = new WAVEFORMATEX { wFormatTag = 3, nChannels = 2, nSamplesPerSec = 44100, wBitsPerSample = 32, nBlockAlign = 8 };
            fmt.nAvgBytesPerSec = 44100 * 8;
            hr = init(b, 0, 0x00040000 | 0x00080000, 3000000, 0, &fmt, null);
            Console.WriteLine($"B plain-float hr=0x{hr:X8}");
        }

        // C) MixFormat
        IntPtr c2 = ActivateClient();
        if (c2 != IntPtr.Zero)
        {
            WAVEFORMATEX* mix = null;
            hr = getMix(c2, &mix);
            Console.WriteLine($"GetMixFormat hr=0x{hr:X8}");
            if (hr == 0 && mix != null)
            {
                Console.WriteLine($"  mix: tag=0x{mix->wFormatTag:X} ch={mix->nChannels} rate={mix->nSamplesPerSec} bits={mix->wBitsPerSample} cb={mix->cbSize}");
                hr = init(c2, 0, 0x00040000 | 0x00080000, 3000000, 0, mix, null);
                Console.WriteLine($"C mixformat hr=0x{hr:X8}");
            }
        }
    }
}
