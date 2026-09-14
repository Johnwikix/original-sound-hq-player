using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>One library maintenance operation, including picker/confirmation and final UI reconciliation.</summary>
internal static class LibraryOperationGate
{
    private static int _busy;
    private static readonly SemaphoreSlim Mutex = new(1, 1);
    public static bool IsBusy => Volatile.Read(ref _busy) != 0;
    public static event Action? Changed;

    public static IDisposable? TryEnter()
    {
        if (!Mutex.Wait(0)) return null;
        Volatile.Write(ref _busy, 1);
        Changed?.Invoke();
        return new Lease();
    }

    public static async Task<IDisposable> EnterAsync(CancellationToken token = default)
    {
        await Mutex.WaitAsync(token).ConfigureAwait(false);
        Volatile.Write(ref _busy, 1);
        Changed?.Invoke();
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke();
            Mutex.Release();
        }
    }
}
