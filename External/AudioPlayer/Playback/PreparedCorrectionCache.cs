using BassPlayerIpc.Shared;
using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;

namespace AudioPlayer.Playback;

/// <summary>只读系数 LRU：最多四套、16 MiB；完整响应频谱只由正在使用它的会话强持有。</summary>
internal static class PreparedCorrectionCache
{
    /// <summary>发布后的所有数组均只读；滤波历史和 FFT 工作区由每个渲染器独占。</summary>
    internal sealed record Prepared(ConvolutionFilter.Coefficients Coefficients, Complex[][]? Spectra, double AutoGainDb);
    private readonly record struct Key(bool Curve, string Identity, int Rate, int Channels, uint ChannelMask);
    private sealed class Entry(Key key, Prepared plain, long bytes)
    {
        internal readonly Key Key = key;
        internal readonly Prepared Plain = plain;
        internal readonly long Bytes = bytes;
        internal WeakReference<Prepared>? Analysis;
    }

    internal const long MaxCachedBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    // 准备串行且等待可取消；不持有 LRU 锁做文件 I/O、FFT 或等待另一首曲目。
    private static readonly SemaphoreSlim Preparation = new(1, 1);
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Entries = new();
    private static readonly LinkedList<Entry> Order = new();
    private static long _bytes, _generation;
    internal static long CachedBytes { get { lock (Gate) return _bytes; } }
    internal static int CachedCount { get { lock (Gate) return Entries.Count; } }

    internal static void Clear()
    {
        lock (Gate)
        {
            ++_generation;
            Entries.Clear();
            Order.Clear();
            _bytes = 0;
        }
    }

    private static void Touch(LinkedListNode<Entry> node)
    {
        Order.Remove(node);
        Order.AddLast(node);
    }

    private static Prepared? Find(Key key, bool analysis)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var node)) return null;
            Prepared? value = node.Value.Plain;
            if (analysis && !(node.Value.Analysis?.TryGetTarget(out value) ?? false)) return null;
            Touch(node);
            return value;
        }
    }

    internal static Prepared Get(DspSettings settings, int rate, int channels, CancellationToken token, uint channelMask = 0)
    {
        token.ThrowIfCancellationRequested();
        long generation = Volatile.Read(ref _generation);
        bool curve = CorrectionCurve.UsesCurve(settings);
        bool analysis = settings.AutoPreamp == true || (!settings.AutoPreamp.HasValue && curve && settings.AutoConvolutionHeadroom);
        // 同一只读句柄用于哈希和解析，避免两次打开之间换文件后缓存错误内容。
        using var stream = curve ? null : File.OpenRead(settings.ImpulsePath);
        string identity = curve ? settings.CurvePoints : HashFile(stream!, token);
        var key = new Key(curve, identity, rate, channels, channelMask);
        if (Find(key, analysis) is { } hit) return hit;
        Preparation.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (Find(key, analysis) is { } prepared) return prepared;
            var plain = Find(key, analysis: false);
            if (stream != null) stream.Position = 0;
            var impulse = curve ? CorrectionCurve.Generate(settings.CurvePoints, rate, token)
                : ImpulseResponse.Read(stream!, token).AtRate(rate, token);
            plain ??= new(ConvolutionFilter.Prepare(impulse, rate, channels, channelMask, token), null, 0);
            Prepared result = plain;
            if (analysis)
            {
                var spectra = new Complex[impulse.Channels.Length][];
                double peak = 1;
                for (int ch = 0; ch < spectra.Length; ch++)
                {
                    token.ThrowIfCancellationRequested();
                    spectra[ch] = ResponseMath.Spectrum(impulse.Channels[ch]);
                    for (int i = 0; i <= spectra[ch].Length / 2; i++) peak = Math.Max(peak, spectra[ch][i].Magnitude);
                }
                result = new(plain.Coefficients, spectra, -20 * Math.Log10(peak));
            }
            token.ThrowIfCancellationRequested();
            Store(key, plain, result, generation);
            return result;
        }
        finally { Preparation.Release(); }
    }

    private static string HashFile(FileStream stream, CancellationToken token)
    {
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("IR file exceeds 64 MiB.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int count;
            while ((count = stream.Read(buffer.AsSpan(0, 65536))) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer.AsSpan(0, count));
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void Store(Key key, Prepared plain, Prepared result, long generation)
    {
        long bytes = StorageBytes(plain.Coefficients) + key.Identity.Length * 2L + 256;
        lock (Gate)
        {
            // 关闭功能后的迟到准备不能重新填充缓存。
            if (generation != _generation || bytes > MaxCachedBytes) return;
            if (!Entries.TryGetValue(key, out var node))
            {
                while (Entries.Count >= 4 || _bytes + bytes > MaxCachedBytes)
                {
                    var oldest = Order.First!;
                    Entries.Remove(oldest.Value.Key);
                    _bytes -= oldest.Value.Bytes;
                    Order.RemoveFirst();
                }
                node = Order.AddLast(new Entry(key, plain, bytes));
                Entries.Add(key, node);
                _bytes += bytes;
            }
            else Touch(node);
            if (result.Spectra != null) node.Value.Analysis = new(result);
        }
    }

    // 按引用去重跨输出声道共享的数组；头部采用保守余量，预算不包含会话工作区。
    private static long StorageBytes(ConvolutionFilter.Coefficients coefficients)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 256 + coefficients.Identity.Length * 2L;
        void Add(Array array, int elementBytes)
        {
            if (seen.Add(array)) bytes += 40 + array.LongLength * elementBytes;
        }
        Add(coefficients.Tiers, IntPtr.Size);
        foreach (var tier in coefficients.Tiers)
        {
            bytes += 128;
            Add(tier.Taps, IntPtr.Size);
            Add(tier.Kernels, IntPtr.Size);
            foreach (var taps in tier.Taps) Add(taps, sizeof(double));
            foreach (var channel in tier.Kernels)
            {
                Add(channel, IntPtr.Size);
                foreach (var kernel in channel) Add(kernel, sizeof(double));
            }
        }
        return bytes;
    }
}
