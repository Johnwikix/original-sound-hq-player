using Microsoft.UI.Dispatching;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>互斥量决定首实例，当前用户的会话管道传递激活；不依赖 HWND 或 AppInstance 注册。</summary>
internal sealed class SingleInstanceEndpoint : IDisposable
{
    private const int MaxPathBytes = 128 * 1024;
    private const int MaxPending = 128;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<string?> _pending = new();
    private NamedPipeServerStream? _server;
    private Task? _listener;
    private DispatcherQueue? _dispatcher;
    private Action<string?>? _handle;
    private Action<Exception>? _reportError;
    private bool _scheduled;
    private bool _closed;
    private bool _disposed;

    public SingleInstanceEndpoint(string mutexName)
    {
        _mutex = new Mutex(false, mutexName);
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        using var process = Process.GetCurrentProcess();
        _pipeName = mutexName.Replace('\\', '_') + ".Activation." + process.SessionId;
    }

    public bool IsPrimary { get; }
    internal string PipeName => _pipeName;

    /// <summary>入口线程同步建好监听端，再启动 Host；UI 未就绪时请求先入队并确认。</summary>
    public void Listen()
    {
        lock (_gate)
        {
            if (!IsPrimary || _closed || _listener is not null) return;
            _server = CreateServer();
            _listener = Task.Run(ListenAsync);
        }
    }

    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    public async Task<bool> SignalAsync(string? path)
    {
        byte[] payload = path is null ? [] : Encoding.UTF8.GetBytes(path);
        if (payload.Length > MaxPathBytes) return false;
        // 只在尚未连接时重试；发送后 ACK 丢失不能再次投递，以免重复播放。
        for (int attempt = 0; attempt < 6; attempt++)
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await client.ConnectAsync(300).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                if (attempt == 5) return false;
                await Task.Delay(150).ConfigureAwait(false);
                continue;
            }
            // 必须由启动方在写请求之前授予权限，目标通过进程 ID 确认，无需共享窗口句柄。
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint processId))
                AllowSetForegroundWindow(processId);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                byte[] header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                if (payload.Length > 0) await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
                await client.ReadExactlyAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
                return header[0] == 1;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                lock (_gate)
                {
                    if (_closed) return;
                    server = _server ??= CreateServer();
                }
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                // 未发送完整请求的连接不能永久占住唯一监听端。
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] header = new byte[sizeof(int)];
                await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length < 0 || length > MaxPathBytes) continue;
                byte[] payload = length == 0 ? [] : new byte[length];
                if (length > 0) await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                string? path = length == 0 ? null : Encoding.UTF8.GetString(payload);
                header[0] = Accept(path) ? (byte)1 : (byte)0;
                await server.WriteAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                if (_stop.IsCancellationRequested) return;
                Report(ex);
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_server, server)) _server = null; }
                server?.Dispose();
            }
        }
    }

    private bool Accept(string? path)
    {
        lock (_gate)
        {
            if (_closed || _pending.Count >= MaxPending) return false;
            _pending.Enqueue(path);
            ScheduleLocked();
            return !_closed;
        }
    }

    /// <summary>UI 线程调用；与入口监听分离，启动早期的文件不会因窗口尚未创建而丢失。</summary>
    public void Attach(DispatcherQueue dispatcher, Action<string?> handle, Action<Exception> reportError)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Activation requires the UI thread.");
        lock (_gate)
        {
            if (_closed || _dispatcher is not null) return;
            _dispatcher = dispatcher;
            _handle = handle;
            _reportError = reportError;
            _scheduled = true;
        }
        Drain();
    }

    private void ScheduleLocked()
    {
        if (_scheduled || _dispatcher is null) return;
        _scheduled = true;
        if (!_dispatcher.TryEnqueue(Drain)) CloseLocked();
    }

    private void Drain()
    {
        for (int count = 0; count < 16; count++)
        {
            string? path;
            Action<string?>? handle;
            lock (_gate)
            {
                if (_closed || !_pending.TryDequeue(out path)) { _scheduled = false; return; }
                handle = _handle;
            }
            try { handle?.Invoke(path); }
            catch (Exception ex) { Report(ex); }
        }
        lock (_gate)
        {
            _scheduled = false;
            if (!_closed && _pending.Count > 0) ScheduleLocked();
        }
    }

    private void Report(Exception error)
    {
        Action<Exception>? report;
        lock (_gate) report = _reportError;
        if (report is not null) report(error);
        else Debug.WriteLine(error);
    }

    private void CloseLocked()
    {
        _closed = true;
        _pending.Clear();
        _dispatcher = null;
        _handle = null;
        _reportError = null;
    }

    public async Task StopAsync()
    {
        Task? listener;
        lock (_gate)
        {
            CloseLocked();
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            _server?.Dispose();
            listener = _listener;
        }
        if (listener is not null)
            try { await listener.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        // 互斥量保留到进程退出；保存尚未完成时不允许另一个 Host 接管。
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        finally
        {
            // Dispose 由持有互斥量的入口/UI 线程调用。
            if (IsPrimary) _mutex.ReleaseMutex();
            _mutex.Dispose();
            _stop.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
