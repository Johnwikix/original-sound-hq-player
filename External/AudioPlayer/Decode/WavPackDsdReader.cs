using System.Runtime.InteropServices;
using AudioPlayer.Interop;

namespace AudioPlayer.Decode;

/// <summary>
/// WavPack DSD lossless decompression, without DSD-to-PCM conversion.
/// Owns the native context on the session's decoder thread; read buffers are allocated once on open.
/// </summary>
internal sealed unsafe class WavPackDsdReader : IDisposable
{
    private IntPtr _context;
    private int[] _samples = [];
    private long _totalFrames;
    private bool _atEnd;
    public int Channels { get; private set; }
    public int ByteRatePerChannel { get; private set; }
    public long TotalMs { get; private set; }

    public static bool IsDsdFile(string path)
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            byte* error = stackalloc byte[80];
            context = WavPackNative.WavpackOpenFileInput(path, error,
                WavPackNative.OpenFileUtf8 | WavPackNative.OpenDsdNative, 0);
            return context != IntPtr.Zero
                && (WavPackNative.WavpackGetQualifyMode(context) & WavPackNative.QModeDsdAudio) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[wavpack] native DSD unavailable: {ex.Message}");
            return false;
        }
        finally { if (context != IntPtr.Zero) WavPackNative.WavpackCloseFile(context); }
    }

    public bool Open(string path)
    {
        Dispose();
        try
        {
            byte* error = stackalloc byte[80];
            new Span<byte>(error, 80).Clear();
            _context = WavPackNative.WavpackOpenFileInput(path, error,
                WavPackNative.OpenFileUtf8 | WavPackNative.OpenDsdNative, 0);
            if (_context == IntPtr.Zero)
            {
                Console.WriteLine($"[wavpack] open failed: {Marshal.PtrToStringUTF8((IntPtr)error)}");
                return false;
            }
            if ((WavPackNative.WavpackGetQualifyMode(_context) & WavPackNative.QModeDsdAudio) == 0)
                return false; // PCM WV must stay on the FFmpeg PCM path.

            Channels = WavPackNative.WavpackGetNumChannels(_context);
            uint rate = WavPackNative.WavpackGetSampleRate(_context); // byte frames/sec, NOT DSD bits/sec
            if (Channels is <= 0 or > 64 || rate == 0 || rate > int.MaxValue / 8) return false;
            ByteRatePerChannel = (int)rate;
            _totalFrames = WavPackNative.WavpackGetNumSamples64(_context);
            TotalMs = _totalFrames > 0 ? (long)Math.Round(_totalFrames * 1000.0 / rate) : 0;
            _samples = new int[8192 * Channels];
            _atEnd = false;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wavpack] open failed: {ex.Message}");
            return false;
        }
        finally { if (_samples.Length == 0) Dispose(); }
    }

    public int ReadInterleaved(Span<byte> destination)
    {
        if (_context == IntPtr.Zero || _atEnd || destination.IsEmpty) return 0;
        int frames = Math.Min(destination.Length, _samples.Length) / Channels;
        if (frames == 0) throw new ArgumentException("Destination must fit a complete DSD byte frame.", nameof(destination));
        uint read;
        fixed (int* samples = _samples)
            read = WavPackNative.WavpackUnpackSamples(_context, samples, (uint)frames);
        if (read > frames || WavPackNative.WavpackGetNumErrors(_context) != 0)
            throw new InvalidDataException("WavPack DSD block failed validation.");
        if (read == 0 && _totalFrames > 0 && WavPackNative.WavpackGetSampleIndex64(_context) < _totalFrames)
            throw new InvalidDataException("WavPack DSD stream ended before its declared sample count.");

        int count = (int)read * Channels;
        // WavPack already normalizes LSBF/MSBF source files to MSB-first interleaved bytes.
        // Do not reverse bits here (the ASIO output handles any driver-specific bit order).
        for (int i = 0; i < count; i++) destination[i] = (byte)_samples[i];
        return count;
    }

    public bool SeekToMs(long ms)
    {
        if (_context == IntPtr.Zero) return false;
        long target = (long)Math.Round(Math.Max(0, ms) * (double)ByteRatePerChannel / 1000);
        if (_totalFrames >= 0 && target >= _totalFrames) { _atEnd = true; return true; }
        if (WavPackNative.WavpackSeekSample64(_context, target) == 0) return false;
        _atEnd = false;
        return true;
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            WavPackNative.WavpackCloseFile(_context);
            _context = IntPtr.Zero;
        }
        _samples = [];
    }
}
