using System.Runtime.CompilerServices;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

namespace AudioPlayer;

/// <summary>Owns the playback engine and its ordered command, streaming-control and state pipes.</summary>
public class PlayerIpcService : IDisposable
{
    private PlaybackEngine? _engine;
    private readonly PipeStateServer _stateServer = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleGate = new();
    private Task? _runTask;
    private int _disposed;
    private bool _stopping;
    private Mutex? _instanceMutex;
    // Only the single ordered control connection accesses this reusable response buffer.
    private readonly byte[] _responseBuffer = new byte[IpcConstants.MaxResponseSize];
    private MessageTypeId _responseType;
    private int _responseLength;
    private (int id, string name, string endpoint)[]? _cachedWasapiDevices;
    private (int id, string name, string endpoint)[]? _cachedAsioDevices;

    public Task StartAsync()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0 || _stopping, this);
            return _runTask ??= RunAsync();
        }
    }

    private async Task RunAsync()
    {
        Task commands = Task.CompletedTask, streaming = Task.CompletedTask, state = Task.CompletedTask;
        Task progress = Task.CompletedTask, monitor = Task.CompletedTask;
        try
        {
            _instanceMutex = new Mutex(true, IpcConstants.MutexName, out bool created);
            if (!created) return;
            var instanceId = Guid.NewGuid();
            _engine = new PlaybackEngine(this);
            PublishDspState(_engine.GetDspState());
            commands = new PipeCommandServer(IpcConstants.ControlPipeName, instanceId, HandleCommand)
                .RunAsync(_stop.Token, singleSession: true);
            streaming = new StreamingServer(_engine, instanceId).RunAsync(_stop.Token);
            state = _stateServer.RunAsync(IpcConstants.StatePipeName, instanceId, _stop.Token);
            progress = PublishProgressAsync(_stop.Token);
            monitor = MonitorClientAliveAsync(_stop.Token);
            Console.WriteLine($"Server ready. Pipe protocol {PipeProtocol.Version}");
            var completed = await Task.WhenAny(commands, streaming, state, progress, monitor).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"Server error: {ex.Message}"); }
        finally
        {
            _stop.Cancel();
            if (_engine is not null)
                await ObserveShutdownAsync(_engine.StopGaplessAsync().WaitAsync(TimeSpan.FromMilliseconds(1500))).ConfigureAwait(false);
            if (_engine is not null) await ObserveShutdownAsync(_engine.StopStreamingAsync()).ConfigureAwait(false);
            await ObserveShutdownAsync(Task.WhenAll(commands, streaming, state, progress, monitor)).ConfigureAwait(false);
            ReleaseResources();
            Console.WriteLine("Server stopped.");
        }
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"IPC shutdown: {ex.Message}"); }
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
        lock (_lifecycleGate)
        {
            _stopping = true;
            if (_disposed == 0) _stop.Cancel();
        }
    }

    private async Task PublishProgressAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            do
            {
                if (_engine!.TryCaptureProgress(out var snapshot)) _stateServer.PublishProgress(snapshot);
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal PipeResponse HandleCommand(CommandId commandId, ReadOnlySpan<byte> payload)
    {
        WriteEmptyResponse(MessageTypeId.Success);
        try
        {
            _engine!.SynchronizeGapless();
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
                    WritePlayStateResponsePayload(_engine.IsPlaying);
                    break;
                case CommandId.SetMusicUrl:
                {
                    var req = BinarySerializer.ReadSetMusicUrlRequest(payload);
                    _engine!.MusicUrl = req.Url ?? string.Empty;
                    WriteEmptyResponse(MessageTypeId.Success);
                    break;
                }
                case CommandId.GetTimeProgress:
                {
                    var (curMs, totalMs) = _engine!.GetTimeProgress();
                    WriteTimeProgressPayload(curMs, totalMs);
                    break;
                }
                case CommandId.ChangePosition:
                {
                    var req = BinarySerializer.ReadChangePositionRequest(payload);
                    _engine!.ChangeWaveChannelTime(req.PositionMs, req.SeekId);
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
                    WriteEqStateResponsePayload(resp);
                    break;
                }
                case CommandId.QueueNext:
                {
                    if (!_engine!.TryQueueNext(GaplessRequest.Read(payload)))
                    {
                        WriteEmptyResponse(MessageTypeId.Failed);
                        break;
                    }
                    var identity = _engine.GetGaplessIdentity();
                    Span<byte> reply = stackalloc byte[16];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply, identity.Token);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply[8..], identity.Epoch);
                    SetResponse(MessageTypeId.Success, reply);
                    break;
                }
                case CommandId.UpdateDsp:
                    _engine!.UpdateDsp(DspProtocol.ReadSettings(payload));
                    break;
                case CommandId.SetFftEnabled:
                    _engine!.SetFftEnabled(BinarySerializer.ReadFftEnabled(payload));
                    break;
                case CommandId.UpdateDeviceCorrections:
                    _engine!.UpdateDeviceCorrections(DeviceCorrectionProtocol.Read(payload));
                    break;
                case CommandId.PreviewDsp:
                    _engine!.PreviewDsp(DspPreview.Read(payload));
                    break;
                case CommandId.GetDspState:
                {
                    Span<byte> state = stackalloc byte[DspProtocol.StateSize];
                    DspProtocol.WriteState(state, _engine!.GetDspState());
                    SetResponse(MessageTypeId.DspState, state);
                    break;
                }
                case CommandId.GetWasapiDevices:
                    HandleGetDevices(MessageTypeId.WasapiDevices, ref _cachedWasapiDevices,
                        () => _engine!.GetWasapiDevices(), payload);
                    break;
                case CommandId.GetAsioDevices:
                    HandleGetDevices(MessageTypeId.AsioDevices, ref _cachedAsioDevices,
                        () => _engine!.GetAsioDevices(), payload);
                    break;
                default:
                    WriteErrorResponse(ErrorCode.InvalidCommand);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Command {commandId} failed: {ex.Message}");
            WriteErrorResponse(ex is ArgumentException ? ErrorCode.InvalidPayload : ErrorCode.Unknown);
        }
        return new(_responseType, _responseBuffer.AsMemory(0, _responseLength));
    }

    private void HandleGetDevices(
        MessageTypeId typeId,
        ref (int id, string name, string endpoint)[]? cache,
        Func<(int id, string name, string endpoint)[]> enumerate,
        ReadOnlySpan<byte> payload)
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

        int maxResp = IpcConstants.MaxResponseSize;
        Span<byte> buf = stackalloc byte[maxResp];
        int offset = BinarySerializer.WriteDeviceListPageHeader(buf, req.Page, (byte)totalPages, (byte)count);
        for (int i = start; i < end; i++)
        {
            var span = buf[offset..];
            offset += BinarySerializer.WriteDeviceEntry(span[..Math.Min(134, span.Length)], devices[i].id, devices[i].name);
            if (typeId is MessageTypeId.WasapiDevices or MessageTypeId.AsioDevices)
            {
                // 设备身份不能截断；超长 ID 应明确失败，而非选择到错误端点。
                if (System.Text.Encoding.UTF8.GetByteCount(devices[i].endpoint) > buf.Length - offset - BinarySerializer.DeviceEntryBaseSize)
                    throw new ArgumentException("Endpoint identity exceeds response capacity");
                offset += BinarySerializer.WriteDeviceEntry(buf[offset..], devices[i].id, devices[i].endpoint);
            }
        }
        SetResponse(typeId, buf[..offset]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteErrorResponse(ErrorCode code)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.FailedResponseSize];
        var resp = new FailedResponse { Code = code };
        BinarySerializer.WriteFailedResponse(buf, resp);
        SetResponse(MessageTypeId.Failed, buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteEmptyResponse(MessageTypeId typeId)
    {
        SetResponse(typeId, ReadOnlySpan<byte>.Empty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteTimeProgressPayload(long currentMs, long totalMs)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.TimeProgressSize];
        BinarySerializer.WriteTimeProgress(buf, currentMs, totalMs);
        SetResponse(MessageTypeId.TimeProgress, buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePlayStateResponsePayload(bool isPlaying)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.PlayStateResponseSize];
        var resp = new PlayStateResponse { IsPlaying = isPlaying };
        BinarySerializer.WritePlayStateResponse(buf, resp);
        SetResponse(MessageTypeId.PlayState, buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteEqStateResponsePayload(EqStateResponse resp)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.EqStateResponseSize];
        BinarySerializer.WriteEqStateResponse(buf, resp);
        SetResponse(MessageTypeId.EqState, buf);
    }

    private void SetResponse(MessageTypeId type, scoped ReadOnlySpan<byte> payload)
    {
        payload.CopyTo(_responseBuffer);
        _responseType = type;
        _responseLength = payload.Length;
    }

    internal void PublishDspState(DspState state) => _stateServer?.PublishDsp(state);

    public void PublishFft(FftSnapshot snapshot) => _stateServer.PublishFft(snapshot);

    public void SendNotification(MessageTypeId typeId, scoped ReadOnlySpan<byte> payload)
        => _stateServer?.PublishNotification(typeId, payload);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PlayStateUpdate(bool isPlaying)
    {
        Span<byte> buf = stackalloc byte[BinarySerializer.PlayStateResponseSize];
        var resp = new PlayStateResponse { IsPlaying = isPlaying };
        BinarySerializer.WritePlayStateResponse(buf, resp);
        SendNotification(MessageTypeId.PlayState, buf);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GaplessTransition(long token, long epoch)
    {
        Span<byte> payload = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload, token);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload[8..], epoch);
        SendNotification(MessageTypeId.GaplessTransition, payload);
    }

    public void PlayBackEnded()
    {
        SendNotification(MessageTypeId.PlayEnded, ReadOnlySpan<byte>.Empty);
    }

    public void Dispose()
    {
        Task? running;
        lock (_lifecycleGate)
        {
            _stopping = true;
            if (_disposed == 0) _stop.Cancel();
            running = _runTask;
        }
        running?.GetAwaiter().GetResult();
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            try { _engine?.Dispose(); }
            catch (Exception ex) { Console.WriteLine($"Engine shutdown: {ex.Message}"); }
            finally
            {
                _stateServer.Dispose();
                _instanceMutex?.Dispose();
                _stop.Dispose();
            }
        }
    }
}
