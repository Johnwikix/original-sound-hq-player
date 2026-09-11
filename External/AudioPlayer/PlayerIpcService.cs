using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

namespace AudioPlayer;

/// <summary>
/// 共享内存邮箱 IPC 服务端 —— BassPlayerSharp MmpIpcService 的协议级移植
/// （版本化邮箱 + 双缓冲通知 + 序号匹配 + 信号量仅作唤醒提示，逐字节兼容）。
/// </summary>
public class PlayerIpcService : IDisposable
{
    private PlaybackEngine? _engine;

    private static readonly long MmfSize = IpcConstants.MmfSize;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;

    private Semaphore? _requestReadySemaphore;
    private Semaphore? _responseReadySemaphore;
    private Semaphore? _notificationReadySemaphore;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;
    private Task? _clientMonitorTask;
    private int _disposeStarted;

    private readonly byte[] _requestBuffer;
    private readonly object _notificationLock = new();

    private int _lastRequestVersion;
    private int _notificationVersion;

    // 设备分页缓存：首次请求枚举，后续页取缓存（bass 同款）
    private (int id, string name, string endpoint)[]? _cachedWasapiDevices;
    private (int id, string name, string endpoint)[]? _cachedAsioDevices;

    private static Mutex? _instanceMutex;

    public PlayerIpcService()
    {
        CheckSingleInstance();
        _cancellationTokenSource = new CancellationTokenSource();
        _requestBuffer = new byte[IpcConstants.MaxRequestSize];
    }

    private static void CheckSingleInstance()
    {
        _instanceMutex = new Mutex(true, IpcConstants.MutexName, out bool mutexCreated);
        if (!mutexCreated) Environment.Exit(0);
    }

    public async Task StartAsync()
    {
        try
        {
            _mmf = MemoryMappedFile.CreateOrOpen(IpcConstants.MmfName, MmfSize);
            _accessor = _mmf.CreateViewAccessor(0, MmfSize);
            _requestReadySemaphore = new Semaphore(0, 1, IpcConstants.RequestSemaphoreName, out _);
            _responseReadySemaphore = new Semaphore(0, 1, IpcConstants.ResponseSemaphoreName, out _);
            _notificationReadySemaphore = new Semaphore(0, 1, IpcConstants.NotificationSemaphoreName, out _);

            Console.WriteLine($"Server ready. MMF: {IpcConstants.MmfName}");
            _engine = new PlaybackEngine(this);
            _listenerTask = Task.Factory.StartNew(() => ListenForRequests(_cancellationTokenSource!.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _clientMonitorTask = Task.Run(() => MonitorClientAliveAsync(_cancellationTokenSource!.Token));
            await Task.WhenAny(_listenerTask, _clientMonitorTask);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
        }
        finally
        {
            _cancellationTokenSource?.Cancel();
            try { await Task.WhenAll(_listenerTask ?? Task.CompletedTask, _clientMonitorTask ?? Task.CompletedTask); } catch (OperationCanceledException) { }
            Dispose();
            Console.WriteLine("Server stopped.");
        }
    }

    private async Task MonitorClientAliveAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Client monitor started...");
        Mutex? clientMutex = null;
        for (int i = 0; i < 100; i++)
        {
            try
            {
                clientMutex = Mutex.OpenExisting(IpcConstants.ClientAliveMutexName);
                break;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
        if (clientMutex == null)
        {
            Console.WriteLine("Warning: Client mutex not found within timeout. Shutting down.");
            Stop();
            return;
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (clientMutex.WaitOne(0))
                {
                    Console.WriteLine("Client exited. Shutting down server...");
                    clientMutex.ReleaseMutex();
                    clientMutex.Dispose();
                    Stop();
                    break;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (AbandonedMutexException)
        {
            Console.WriteLine("Client crashed. Shutting down...");
            Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client monitor error: {ex.Message}");
        }
        finally
        {
            clientMutex?.Dispose();
        }
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();

    }

    private void ListenForRequests(CancellationToken cancellationToken)
    {
        Console.WriteLine("Listening for requests...");
        WaitHandle[] waits = [cancellationToken.WaitHandle, _requestReadySemaphore!];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (WaitHandle.WaitAny(waits) == 0) break;
                if (cancellationToken.IsCancellationRequested) break;
                if (_accessor == null) continue;

                int version = IpcEnvelope.ReadVersion(_accessor, IpcConstants.RequestVersionOffset);
                if (version == _lastRequestVersion) continue;
                _lastRequestVersion = version;

                byte sequenceId = IpcEnvelope.ReadSequenceId(_accessor, IpcConstants.RequestBufferOffset);

                int payloadLen = IpcEnvelope.ReadPayload(
                    _accessor, IpcConstants.RequestBufferOffset,
                    _requestBuffer,
                    IpcConstants.MaxRequestSize - IpcConstants.EnvelopeHeaderSize);

                if (payloadLen < 0)
                {
                    WriteErrorResponse(ErrorCode.InvalidPayload, sequenceId);
                    SignalResponseReady();
                    continue;
                }

                var commandId = IpcEnvelope.ReadCommandId(_accessor, IpcConstants.RequestBufferOffset);
                HandleCommand(commandId, _requestBuffer.AsSpan(0, payloadLen), sequenceId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"Request loop error: {ex.Message}");
                if (cancellationToken.WaitHandle.WaitOne(500)) break;
            }
        }
    }

    private void HandleCommand(CommandId commandId, ReadOnlySpan<byte> payload, byte sequenceId)
    {
        WriteEmptyResponse(MessageTypeId.Success, sequenceId);
        try
        {
            switch (commandId)
            {
                case CommandId.Play:
                {
                    var req = BinarySerializer.ReadPlayRequest(payload);
                    _engine!.PlayMusic(req.Url ?? string.Empty);
                    break;
                }
                case CommandId.PlayButton:
                    _engine!.PlayButton();
                    WritePlayStateResponsePayload(_engine.IsPlaying, sequenceId);
                    break;
                case CommandId.SetMusicUrl:
                {
                    var req = BinarySerializer.ReadSetMusicUrlRequest(payload);
                    _engine!.MusicUrl = req.Url ?? string.Empty;
                    WriteEmptyResponse(MessageTypeId.Success, sequenceId);
                    break;
                }
                case CommandId.GetTimeProgress:
                {
                    var (curMs, totalMs) = _engine!.GetTimeProgress();
                    WriteTimeProgressPayload(curMs, totalMs, sequenceId);
                    break;
                }
                case CommandId.ChangePosition:
                {
                    var req = BinarySerializer.ReadChangePositionRequest(payload);
                    _engine!.ChangeWaveChannelTime(req.PositionMs);
                    break;
                }
                case CommandId.ChangeVolume:
                {
                    var req = BinarySerializer.ReadChangeVolumeRequest(payload);
                    _engine!.SetVolume(req.Volume);
                    break;
                }
                case CommandId.MusicEnd:
                    _engine!.MusicEnd();
                    break;
                case CommandId.FadeOut:
                    _engine!.FadeOut();
                    break;
                case CommandId.UpdateSettings:
                {
                    var settings = BinarySerializer.ReadIpcSetting(payload);
                    _engine!.UpdateSettings(settings);
                    break;
                }
                case CommandId.UpdateEq:
                {
                    var req = BinarySerializer.ReadUpdateEqRequest(payload);
                    var resp = _engine!.SetEqualizerState(req);
                    WriteEqStateResponsePayload(resp, sequenceId);
                    break;
                }
                case CommandId.UpdateDsp:
                    _engine!.UpdateDsp(DspProtocol.ReadSettings(payload));
                    break;
                case CommandId.GetDspState:
                {
                    Span<byte> state = stackalloc byte[DspProtocol.StateSize];
                    DspProtocol.WriteState(state, _engine!.GetDspState());
                    IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset,
                        MessageTypeId.DspState, sequenceId, state, IpcConstants.MaxResponseSize);
                    break;
                }
                case CommandId.GetWasapiDevices:
                    HandleGetDevices(MessageTypeId.WasapiDevices, ref _cachedWasapiDevices,
                        () => _engine!.GetWasapiDevices(), payload, sequenceId);
                    break;
                case CommandId.GetAsioDevices:
                    HandleGetDevices(MessageTypeId.AsioDevices, ref _cachedAsioDevices,
                        () => _engine!.GetAsioDevices().Select(d => (d.id, d.name, string.Empty)).ToArray(), payload, sequenceId);
                    break;
                default:
                    WriteErrorResponse(ErrorCode.InvalidCommand, sequenceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Command {commandId} failed: {ex.Message}");
            WriteErrorResponse(ex is ArgumentException ? ErrorCode.InvalidPayload : ErrorCode.Unknown, sequenceId);
        }
        SignalResponseReady();
    }

    private void HandleGetDevices(
        MessageTypeId typeId,
        ref (int id, string name, string endpoint)[]? cache,
        Func<(int id, string name, string endpoint)[]> enumerate,
        ReadOnlySpan<byte> payload,
        byte sequenceId)
    {
        var req = BinarySerializer.ReadGetDevicesRequest(payload);
        if (req.Page == 0 || cache == null) cache = enumerate();
        var devices = cache;

        int total = devices.Length;
        int perPage = 1;
        int totalPages = total == 0 ? 1 : (total + perPage - 1) / perPage;
        if (req.Page >= totalPages) req.Page = 0;

        int start = req.Page * perPage;
        int end = Math.Min(start + perPage, total);
        int count = end - start;

        int maxResp = IpcConstants.MaxResponseSize - IpcConstants.EnvelopeHeaderSize;
        Span<byte> buf = stackalloc byte[maxResp];
        int offset = BinarySerializer.WriteDeviceListPageHeader(buf, req.Page, (byte)totalPages, (byte)count);
        for (int i = start; i < end; i++)
        {
            var span = buf[offset..];
            offset += BinarySerializer.WriteDeviceEntry(span[..Math.Min(134, span.Length)], devices[i].id, devices[i].name);
            if (typeId == MessageTypeId.WasapiDevices)
            {
                // 设备身份不能截断；超长 ID 应明确失败，而非选择到错误端点。
                if (System.Text.Encoding.UTF8.GetByteCount(devices[i].endpoint) > buf.Length - offset - BinarySerializer.DeviceEntryBaseSize)
                    throw new ArgumentException("Endpoint identity exceeds response capacity");
                offset += BinarySerializer.WriteDeviceEntry(buf[offset..], devices[i].id, devices[i].endpoint);
            }
        }
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, typeId, sequenceId, buf[..offset], IpcConstants.MaxResponseSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteErrorResponse(ErrorCode code, byte sequenceId)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.FailedResponseSize];
        var resp = new FailedResponse { Code = code };
        BinarySerializer.WriteFailedResponse(buf, resp);
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, MessageTypeId.Failed, sequenceId, buf, IpcConstants.MaxResponseSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteEmptyResponse(MessageTypeId typeId, byte sequenceId)
    {
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, typeId, sequenceId, ReadOnlySpan<byte>.Empty, IpcConstants.MaxResponseSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteTimeProgressPayload(long currentMs, long totalMs, byte sequenceId)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.TimeProgressSize];
        BinarySerializer.WriteTimeProgress(buf, currentMs, totalMs);
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, MessageTypeId.TimeProgress, sequenceId, buf, IpcConstants.MaxResponseSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePlayStateResponsePayload(bool isPlaying, byte sequenceId)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.PlayStateResponseSize];
        var resp = new PlayStateResponse { IsPlaying = isPlaying };
        BinarySerializer.WritePlayStateResponse(buf, resp);
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, MessageTypeId.PlayState, sequenceId, buf, IpcConstants.MaxResponseSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteEqStateResponsePayload(EqStateResponse resp, byte sequenceId)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.EqStateResponseSize];
        BinarySerializer.WriteEqStateResponse(buf, resp);
        IpcEnvelope.WriteResponse(_accessor!, IpcConstants.ResponseBufferOffset, MessageTypeId.EqState, sequenceId, buf, IpcConstants.MaxResponseSize);
    }

    private void SignalResponseReady()
    {
        if (_accessor == null) return;
        int version = _lastRequestVersion; // 完整请求版本作为执行确认，避免 byte 序号回绕歧义。
        IpcEnvelope.PublishVersion(_accessor, IpcConstants.ResponseVersionOffset, version);
        try { _responseReadySemaphore!.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void SendNotification(MessageTypeId typeId, scoped ReadOnlySpan<byte> payload)
    {
        if (_accessor == null) return;
        try
        {
            lock (_notificationLock)
            {
                int version = ++_notificationVersion;
                long offset = IpcEnvelope.NotificationSlotOffset(version);
                IpcEnvelope.WriteResponse(_accessor, offset, typeId, 0, payload, IpcConstants.MaxNotificationSize);
                IpcEnvelope.PublishVersion(_accessor, IpcConstants.NotificationVersionOffset, version);
                try { _notificationReadySemaphore!.Release(); }
                catch (SemaphoreFullException) { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendNotification failed: {ex.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PlayStateUpdate(bool isPlaying)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.PlayStateResponseSize];
        var resp = new PlayStateResponse { IsPlaying = isPlaying };
        BinarySerializer.WritePlayStateResponse(buf, resp);
        SendNotification(MessageTypeId.PlayState, buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PlayBackEnded()
    {
        SendNotification(MessageTypeId.PlayEnded, ReadOnlySpan<byte>.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _cancellationTokenSource?.Cancel();
        _listenerTask?.GetAwaiter().GetResult();
        _engine?.Dispose();
        _accessor?.Dispose();
        _mmf?.Dispose();
        _requestReadySemaphore?.Dispose();
        _responseReadySemaphore?.Dispose();
        _notificationReadySemaphore?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}
