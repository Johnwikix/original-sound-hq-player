using System.Buffers;
using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>
/// Pulls stereo PCM from the render callback into a bounded SPSC queue and computes
/// windows off the audio thread. The render callback never waits for this worker.
/// </summary>
internal sealed class FftAnalyzer : IDisposable
{
    internal const int FftSize = 1024;
    internal const int HopSize = 512;
    internal const int BinCount = FftSize / 2 + 1;

    private const int QueueCapacity = FftSize * 8;
    private readonly Action<FftSnapshot> _publish;
    // Allocate FFT storage only while the feature is enabled. The analyzer itself
    // lives with the player, so eager buffers would tax every playback session.
    private float[]? _leftQueue;
    private float[]? _rightQueue;
    private double[]? _leftWork;
    private double[]? _rightWork;
    private float[]? _window;
    private AutoResetEvent? _wake;
    private readonly object _lifecycleGate = new();
    private long _write;
    private long _read;
    private long _epoch;
    private long _generation;
    private long _sequence;
    private long _droppedSamples;
    private int _sampleRate;
    private int _enabled;
    private int _stopWorker;
    private int _disposed;
    private Thread? _worker;

    internal FftAnalyzer(Action<FftSnapshot> publish)
    {
        _publish = publish;
    }

    internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    internal long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    internal void SetEnabled(bool enabled)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (enabled)
            {
                if (Volatile.Read(ref _enabled) != 0) return;
                EnsureBuffersLocked();
                Volatile.Write(ref _enabled, 1);
                Interlocked.Increment(ref _generation);
                long write = Interlocked.Read(ref _write);
                Volatile.Write(ref _read, write);
                Volatile.Write(ref _stopWorker, 0);
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "fft-analyzer",
                };
                _worker.Start();
                PublishUnavailable(discontinuity: true);
                return;
            }

            if (Interlocked.Exchange(ref _enabled, 0) == 0) return;
            StopWorkerLocked();
            ReleaseBuffersLocked();
            Interlocked.Increment(ref _generation);
            long current = Interlocked.Read(ref _write);
            Volatile.Write(ref _read, current);
            PublishUnavailable(discontinuity: true);
        }
    }

    internal void Reset(long epoch, int sampleRate)
    {
        Volatile.Write(ref _epoch, Math.Max(0, epoch));
        Volatile.Write(ref _sampleRate, Math.Max(0, sampleRate));
        Interlocked.Increment(ref _generation);
        long write = Interlocked.Read(ref _write);
        Volatile.Write(ref _read, write);
        if (IsEnabled) PublishUnavailable(discontinuity: true);
        try { Volatile.Read(ref _wake)?.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Called by the active PCM render callback. It must remain non-blocking.</summary>
    internal void Capture(ReadOnlySpan<double> interleaved, int frames, int channels, uint channelMask, int sampleRate)
    {
        if (Volatile.Read(ref _enabled) == 0 || frames <= 0 || channels <= 0) return;
        float[]? leftQueue = Volatile.Read(ref _leftQueue);
        float[]? rightQueue = Volatile.Read(ref _rightQueue);
        if (leftQueue is null || rightQueue is null) return;
        _ = channelMask; // Channel order is normalized to a stable stereo pair below.
        if (sampleRate > 0 && Volatile.Read(ref _sampleRate) != sampleRate)
            Volatile.Write(ref _sampleRate, sampleRate);

        int take = Math.Min(frames, QueueCapacity);
        long write = Interlocked.Read(ref _write);
        long read = Volatile.Read(ref _read);
        long used = write - read;
        if (used < 0 || used > QueueCapacity || take > QueueCapacity - used)
        {
            Interlocked.Add(ref _droppedSamples, frames);
            return;
        }

        int sourceOffset = frames - take;
        for (int i = 0; i < take; i++)
        {
            int source = (sourceOffset + i) * channels;
            double left = interleaved[source];
            double right = channels > 1 ? interleaved[source + 1] : left;
            if (channels > 2)
            {
                double center = interleaved[source + 2] * 0.7071067811865476;
                left += center;
                right += center;
                if (channels > 3)
                {
                    double lfe = interleaved[source + 3] * 0.25;
                    left += lfe;
                    right += lfe;
                }
                for (int channel = 4; channel < channels; channel++)
                {
                    double surround = interleaved[source + channel] * 0.5;
                    if ((channel & 1) == 0) left += surround;
                    else right += surround;
                }
            }
            int target = (int)((write + i) % QueueCapacity);
            leftQueue[target] = double.IsFinite(left) ? (float)left : 0;
            rightQueue[target] = double.IsFinite(right) ? (float)right : 0;
        }

        // Publish the write cursor only after all samples are visible to the worker.
        Volatile.Write(ref _write, write + take);
        // Wake only on an empty-to-nonempty transition; the worker otherwise polls
        // the already active queue and the render callback avoids repeated kernel calls.
        if (used == 0)
        {
            try { Volatile.Read(ref _wake)?.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void WorkerLoop()
    {
        while (Volatile.Read(ref _stopWorker) == 0)
        {
            if (Volatile.Read(ref _enabled) == 0) break;
            if (!TryReadWindow(out long generation, out long epoch, out int sampleRate))
            {
                try { Volatile.Read(ref _wake)?.WaitOne(20); }
                catch (ObjectDisposedException) { return; }
                continue;
            }

            ComputeSpectrum(_leftWork!, out float[] left);
            ComputeSpectrum(_rightWork!, out float[] right);
            if (generation != Volatile.Read(ref _generation) || Volatile.Read(ref _enabled) == 0)
            {
                ArrayPool<float>.Shared.Return(left);
                ArrayPool<float>.Shared.Return(right);
                continue;
            }

            var snapshot = new FftSnapshot(
                Interlocked.Increment(ref _sequence), epoch, sampleRate, FftSize, HopSize,
                FftFrameFlags.Available, left, right);
            try { _publish(snapshot); }
            catch (Exception ex) { Console.WriteLine($"[fft] publish failed: {ex.Message}"); }
            finally
            {
                ArrayPool<float>.Shared.Return(left);
                ArrayPool<float>.Shared.Return(right);
            }
        }
    }

    private bool TryReadWindow(out long generation, out long epoch, out int sampleRate)
    {
        generation = Volatile.Read(ref _generation);
        epoch = Volatile.Read(ref _epoch);
        sampleRate = Volatile.Read(ref _sampleRate);
        long read = Volatile.Read(ref _read);
        long write = Interlocked.Read(ref _write);
        if (write - read < FftSize) return false;

        float[]? leftQueue = Volatile.Read(ref _leftQueue);
        float[]? rightQueue = Volatile.Read(ref _rightQueue);
        double[]? leftWork = Volatile.Read(ref _leftWork);
        double[]? rightWork = Volatile.Read(ref _rightWork);
        float[]? window = Volatile.Read(ref _window);
        if (leftQueue is null || rightQueue is null || leftWork is null || rightWork is null || window is null)
            return false;

        for (int i = 0; i < FftSize; i++)
        {
            int index = (int)((read + i) % QueueCapacity);
            leftWork[i] = leftQueue[index] * window[i];
            rightWork[i] = rightQueue[index] * window[i];
        }
        Volatile.Write(ref _read, read + HopSize);
        return true;
    }

    private static void ComputeSpectrum(double[] values, out float[] result)
    {
        result = ArrayPool<float>.Shared.Rent(BinCount);
        Span<double> imaginary = stackalloc double[FftSize];
        Fft(values, imaginary);
        const double scale = 2.0 / FftSize;
        for (int i = 0; i < BinCount; i++)
        {
            double magnitude = Math.Sqrt(values[i] * values[i] + imaginary[i] * imaginary[i]) * scale;
            if (i is 0 or FftSize / 2) magnitude *= 0.5;
            result[i] = double.IsFinite(magnitude) ? (float)Math.Max(0, magnitude) : 0;
        }
    }

    private static void Fft(double[] real, Span<double> imaginary)
    {
        for (int i = 1, j = 0; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            double angle = -2 * Math.PI / size;
            double stepReal = Math.Cos(angle), stepImaginary = Math.Sin(angle);
            for (int start = 0; start < FftSize; start += size)
            {
                double rootReal = 1, rootImaginary = 0;
                int half = size / 2;
                for (int i = 0; i < half; i++)
                {
                    int even = start + i, odd = even + half;
                    double oddReal = real[odd] * rootReal - imaginary[odd] * rootImaginary;
                    double oddImaginary = real[odd] * rootImaginary + imaginary[odd] * rootReal;
                    double evenReal = real[even], evenImaginary = imaginary[even];
                    real[even] = evenReal + oddReal;
                    imaginary[even] = evenImaginary + oddImaginary;
                    real[odd] = evenReal - oddReal;
                    imaginary[odd] = evenImaginary - oddImaginary;
                    (rootReal, rootImaginary) = (rootReal * stepReal - rootImaginary * stepImaginary,
                        rootReal * stepImaginary + rootImaginary * stepReal);
                }
            }
        }
    }

    private void PublishUnavailable(bool discontinuity)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var flags = discontinuity ? FftFrameFlags.Discontinuity : FftFrameFlags.None;
        try
        {
            _publish(new FftSnapshot(
                Interlocked.Increment(ref _sequence), Volatile.Read(ref _epoch),
                Volatile.Read(ref _sampleRate), 0, 0, flags, [], []));
        }
        catch (Exception ex) { Console.WriteLine($"[fft] state publish failed: {ex.Message}"); }
    }

    private void StopWorkerLocked()
    {
        Volatile.Write(ref _stopWorker, 1);
        try { Volatile.Read(ref _wake)?.Set(); }
        catch (ObjectDisposedException) { }
        var worker = _worker;
        // Do not release work arrays until a publisher that may still be using
        // them has returned. The completed join is the disable lifecycle boundary.
        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join();
        _worker = null;
    }

    private void EnsureBuffersLocked()
    {
        if (_leftQueue is not null) return;
        var leftQueue = new float[QueueCapacity];
        var rightQueue = new float[QueueCapacity];
        var leftWork = new double[FftSize];
        var rightWork = new double[FftSize];
        var window = new float[FftSize];
        var wake = new AutoResetEvent(false);
        for (int i = 0; i < FftSize; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        _leftQueue = leftQueue;
        _rightQueue = rightQueue;
        _leftWork = leftWork;
        _rightWork = rightWork;
        _window = window;
        _wake = wake;
    }

    private void ReleaseBuffersLocked()
    {
        Volatile.Write(ref _leftQueue, null);
        Volatile.Write(ref _rightQueue, null);
        Volatile.Write(ref _leftWork, null);
        Volatile.Write(ref _rightWork, null);
        Volatile.Write(ref _window, null);
        AutoResetEvent? wake = Interlocked.Exchange(ref _wake, null);
        wake?.Dispose();
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _enabled, 0);
            StopWorkerLocked();
            ReleaseBuffersLocked();
        }
    }
}
