using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class IpcService : IDisposable
    {
        private static readonly long MmfSize = IpcConstants.MmfSize;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private Semaphore? _requestReadySemaphore;
        private Semaphore? _responseReadySemaphore;
        private Semaphore? _notificationReadySemaphore;

        private readonly Dictionary<int, string> _wasapiEndpoints = new();
        /// <summary>获取本次枚举中设备索引对应的稳定端点 ID。</summary>
        public string? GetWasapiEndpointId(int id) => _wasapiEndpoints.GetValueOrDefault(id);
        private MailboxClient? _transport;
        private int _disposed;

        private CancellationTokenSource? _notificationCts;
        private Task? _notificationListenerTask;
        private CancellationTokenSource? _serverMonitorCts;
        private Task? _serverMonitorTask;
        private readonly ILogger<IpcService> _logger;
        private AppViewModel AppViewModel { get; }

        private int _lastNotificationVersion;
        private readonly byte[] _notificationBuffer = new byte[IpcConstants.MaxNotificationSize];

        /// <summary>
        /// Raised on the notification listener thread when a notification arrives.
        /// Contract: the <see cref="ReadOnlyMemory{T}"/> payload points into a reused
        /// zero-allocation buffer and is only valid DURING the handler invocation -
        /// handlers must copy (or parse synchronously) before returning.
        /// </summary>
        public event Action<MessageTypeId, ReadOnlyMemory<byte>>? NotificationReceived;

        public IpcService(AppViewModel appViewModel, ILogger<IpcService> logger)
        {
            AppViewModel = appViewModel;
            _logger = logger;
        }

        public async Task InitializingAsync()
        {
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    _mmf = MemoryMappedFile.OpenExisting(IpcConstants.MmfName);
                    _accessor = _mmf.CreateViewAccessor(0, MmfSize);
                    _requestReadySemaphore = Semaphore.OpenExisting(IpcConstants.RequestSemaphoreName);
                    _responseReadySemaphore = Semaphore.OpenExisting(IpcConstants.ResponseSemaphoreName);
                    _notificationReadySemaphore = Semaphore.OpenExisting(IpcConstants.NotificationSemaphoreName);
                    _transport = new MailboxClient(_accessor, _requestReadySemaphore, _responseReadySemaphore);
                    _transport.CommandFailed += command => _logger.LogWarning("Audio command {Command} was rejected", command);
                    _transport.Faulted += exception => _logger.LogError(exception, "Audio IPC transport stopped");
                    StartNotificationListener();
                    StartServerMonitor();
                    return;
                }
                catch
                {
                    _transport?.Dispose(); _transport = null;
                    _accessor?.Dispose(); _accessor = null;
                    _mmf?.Dispose(); _mmf = null;
                    _requestReadySemaphore?.Dispose(); _requestReadySemaphore = null;
                    _responseReadySemaphore?.Dispose(); _responseReadySemaphore = null;
                    _notificationReadySemaphore?.Dispose(); _notificationReadySemaphore = null;
                    await Task.Delay(100);
                }
            }
            _logger.LogCritical("IPC connection failed after retries - core process unavailable, exiting.");
            ShutdownApp();
        }

        public async Task InitializeMusic(Music? music)
        {
            // 将旧版名称/索引迁移为稳定 ID，避免用户尚未打开设置页时选错端点。
            if (string.IsNullOrEmpty(AppSettings.WasapiEndpointId) && AppSettings.OutputMode.StartsWith("Wasapi", StringComparison.Ordinal))
            {
                var devices = await GetWasapiDevices();
                int matches = 0, selectedId = -1;
                foreach (var device in devices)
                {
                    if (device.name != AppSettings.DeviceName) continue;
                    matches++; selectedId = device.id;
                }
                AppSettings.BassOutputDeviceId = matches == 1 ? selectedId : -1;
                if (matches == 1) AppSettings.WasapiEndpointId = GetWasapiEndpointId(selectedId);
                else _logger.LogWarning("Saved WASAPI device is missing or ambiguous; using default endpoint");
            }
            if (music is not null)
                await SetMusicUrl(music.Path);
            UpdateEq();
            UpdateSettings();
            UpdateDsp();
        }

        private void StartNotificationListener()
        {
            _notificationCts = new CancellationTokenSource();
            _notificationListenerTask = Task.Factory.StartNew(() => ListenForNotifications(_notificationCts.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Watches the core process's single-instance mutex. When the core exits or
        /// crashes the mutex becomes acquirable; by design either side exiting shuts
        /// down the whole program, so we trigger a graceful app exit.
        /// </summary>
        private void StartServerMonitor()
        {
            _serverMonitorCts = new CancellationTokenSource();
            _serverMonitorTask = Task.Run(() => MonitorServerAliveAsync(_serverMonitorCts.Token));
        }

        private async Task MonitorServerAliveAsync(CancellationToken cancellationToken)
        {
            Mutex? serverMutex = null;
            for (int i = 0; i < 200 && serverMutex == null; i++)
            {
                try { serverMutex = Mutex.OpenExisting(IpcConstants.MutexName); }
                catch (WaitHandleCannotBeOpenedException)
                {
                    try { await Task.Delay(100, cancellationToken); }
                    catch (OperationCanceledException) { return; }
                }
            }
            if (serverMutex == null)
            {
                _logger.LogWarning("Server alive mutex not found within timeout");
                return;
            }
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (serverMutex.WaitOne(0))
                        {
                            serverMutex.ReleaseMutex();
                            _logger.LogWarning("Core process exited; shutting down application.");
                            ShutdownApp();
                            break;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        _logger.LogWarning("Core process crashed; shutting down application.");
                        ShutdownApp();
                        break;
                    }
                    try { await Task.Delay(200, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                serverMutex.Dispose();
            }
        }

        /// <summary>
        /// Triggers a graceful app exit, dispatching to the UI thread when the window
        /// exists; falls back to a direct call during startup (Current_Exit is
        /// exception-guarded and always exits the process).
        /// </summary>
        private static void ShutdownApp()
        {
            var window = App.MainWindow;
            if (window is not null && window.DispatcherQueue.TryEnqueue(() => _ = App.Current_Exit()))
                return;
            _ = App.Current_Exit();
        }

        private void ListenForNotifications(CancellationToken cancellationToken)
        {
            WaitHandle[] waits = [cancellationToken.WaitHandle, _notificationReadySemaphore!];
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (WaitHandle.WaitAny(waits, 1000) == 0) break;

                    int version = IpcEnvelope.ReadVersion(_accessor!, IpcConstants.NotificationVersionOffset);
                    if (version == _lastNotificationVersion) continue;

                    // Double-buffered: the slot is selected by version parity. The payload
                    // is only trusted after re-reading the version: if it advanced while we
                    // read, the slot may have been overwritten (same-parity reuse), so retry
                    // with the newer version instead of parsing torn data.
                    long slot = IpcEnvelope.NotificationSlotOffset(version);
                    var typeId = IpcEnvelope.ReadMessageTypeId(_accessor!, slot);
                    int payloadLen = IpcEnvelope.ReadPayload(
                        _accessor!, slot,
                        _notificationBuffer,
                        IpcConstants.MaxNotificationSize - IpcConstants.EnvelopeHeaderSize);

                    if (IpcEnvelope.ReadVersion(_accessor!, IpcConstants.NotificationVersionOffset) != version)
                        continue;

                    _lastNotificationVersion = version;

                    if (payloadLen < 0) continue;
                    if (payloadLen > 0)
                    {
                        var mem = new ReadOnlyMemory<byte>(_notificationBuffer, 0, payloadLen);
                        NotificationReceived?.Invoke(typeId, mem);
                    }
                    else
                    {
                        NotificationReceived?.Invoke(typeId, ReadOnlyMemory<byte>.Empty);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Notification listener error");
                    if (cancellationToken.WaitHandle.WaitOne(500)) break;
                }
            }
        }

        // ──────────────── Core send ────────────────

        public async Task<MessageTypeId> SendCommandAsync(CommandId commandId, ReadOnlyMemory<byte> payload)
        {
            return (await SendWithResponseAsync(commandId, payload, Array.Empty<byte>())).Type;
        }

        public Task<(MessageTypeId Type, int ResponseLen)> SendWithResponseAsync(
            CommandId commandId, ReadOnlyMemory<byte> payload, byte[] responseBuffer,
            int timeoutMs = 1000, bool skipIfBusy = false)
            => _transport?.RequestAsync(commandId, payload.Span, responseBuffer, timeoutMs, skipIfBusy)
                ?? Task.FromResult((MessageTypeId.Failed, 0));

        /// <summary>同步复制到发送队列；同类高频状态在有序屏障内合并。</summary>
        private void Publish(CommandId commandId, ReadOnlySpan<byte> payload)
        {
            bool coalesce = commandId is CommandId.ChangeVolume or CommandId.UpdateEq or CommandId.UpdateDsp;
            if (_transport?.Publish(commandId, payload, coalesce) != true)
                _logger.LogWarning("Audio command {Command} could not be queued", commandId);
        }

        // ──────────────── Public API ────────────────

        public void Play(string musicUrl)
        {
            var req = new PlayRequest { Url = musicUrl };
            Span<byte> buf = stackalloc byte[BinarySerializer.PlayRequestSize];
            int len = BinarySerializer.WritePlayRequest(buf, req);
            Publish(CommandId.Play, buf[..len]);
        }

        /// <summary>
        /// Toggles play/pause. The response carries the authoritative playback state
        /// (the server echoes it after the state change completes); returns null when
        /// the round-trip fails, in which case the UI is corrected by notifications.
        /// </summary>
        public async Task<bool?> PlayButton()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BinarySerializer.PlayStateResponseSize);
            try
            {
                var (type, length) = await SendWithResponseAsync(CommandId.PlayButton, ReadOnlyMemory<byte>.Empty, buffer);
                return type == MessageTypeId.PlayState && length == BinarySerializer.PlayStateResponseSize
                    ? BinarySerializer.ReadPlayStateResponse(buffer).IsPlaying : null;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public void UpdateSettings(bool isSettingChanged = false)
        {
            var settings = new IpcSetting
            {
                OutputMode = AppSettings.OutputMode,
                BassOutputDeviceId = AppSettings.BassOutputDeviceId,
                WasapiEndpointId = AppSettings.WasapiEndpointId,
                BassASIODeviceId = AppSettings.BassASIODeviceId,
                Latency = AppViewModel.Latency,
                IsDopEnabled = AppViewModel.IsDopEnabled,
                DsdGain = AppViewModel.DsdGain,
                DsdPcmFreq = AppViewModel.DsdPcmFreq,
                IsEqualizerEnabled = AppSettings.IsEqualizerEnabled,
                Volume = (float)(App.Services.GetRequiredService<AppViewModel>().Volume / 100.0),
                IsSettingChanged = isSettingChanged,
                IsFadeEnabled = AppViewModel.IsFadeEnabled,
            };
            Span<byte> buf = stackalloc byte[BinarySerializer.IpcSettingSize];
            int len = BinarySerializer.WriteIpcSetting(buf, settings);
            Publish(CommandId.UpdateSettings, buf[..len]);
        }

        /// <summary>发送音效快照，不触发输出设备重建。</summary>
        public void UpdateDsp()
        {
            Span<byte> buffer = stackalloc byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(buffer, AppSettings.Dsp);
            Publish(CommandId.UpdateDsp, buffer);
        }

        /// <summary>读取实际输出和音效状态；每次请求独占响应缓冲，允许不同界面并发刷新。</summary>
        public async Task<DspState?> GetDspStateAsync()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(DspProtocol.StateSize);
            try
            {
                var (type, length) = await SendWithResponseAsync(CommandId.GetDspState,
                    ReadOnlyMemory<byte>.Empty, buffer, timeoutMs: 1000);
                return type == MessageTypeId.DspState && length == DspProtocol.StateSize
                    ? DspProtocol.ReadState(buffer.AsSpan(0, length)) : null;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        /// <summary>Fire-and-forget full equalizer state sync (slider drags, startup).</summary>
        public void UpdateEq()
        {
            var req = ConvertBandsToUpdateEqRequest();
            Span<byte> buf = stackalloc byte[BinarySerializer.UpdateEqRequestSize];
            BinarySerializer.WriteUpdateEqRequest(buf, req);
            Publish(CommandId.UpdateEq, buf);
        }

        /// <summary>
        /// Sends the full equalizer state and awaits the server's real applied state.
        /// IsEnabled comes back false when the output mode rejects the EQ (e.g. DSD over
        /// exclusive output); callers should roll the UI switch back in that case.
        /// Returns null when the round-trip fails.
        /// </summary>
        public async Task<bool?> UpdateEqAsync()
        {
            var req = ConvertBandsToUpdateEqRequest();
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.UpdateEqRequestSize);
            var response = ArrayPool<byte>.Shared.Rent(BinarySerializer.EqStateResponseSize);
            try
            {
                BinarySerializer.WriteUpdateEqRequest(buf, req);
                var (resType, length) = await SendWithResponseAsync(CommandId.UpdateEq,
                    new ReadOnlyMemory<byte>(buf, 0, BinarySerializer.UpdateEqRequestSize),
                    response, timeoutMs: 1000);
                return resType == MessageTypeId.EqState && length == BinarySerializer.EqStateResponseSize
                    ? BinarySerializer.ReadEqStateResponse(response).IsEnabled
                    : null;
            }
            finally { ArrayPool<byte>.Shared.Return(buf); ArrayPool<byte>.Shared.Return(response); }
        }

        /// <summary>
        /// 频段数组 → IPC 请求。协议传输每段增益和 Q（float32）。
        /// </summary>
        private static UpdateEqRequest ConvertBandsToUpdateEqRequest()
        {
            var bands = AppSettings.EqualizerBands;
            return new UpdateEqRequest
            {
                IsEnabled = AppSettings.IsEqualizerEnabled,
                Band0 = (float)bands[0].GainDb,
                Band1 = (float)bands[1].GainDb,
                Band2 = (float)bands[2].GainDb,
                Band3 = (float)bands[3].GainDb,
                Band4 = (float)bands[4].GainDb,
                Band5 = (float)bands[5].GainDb,
                Band6 = (float)bands[6].GainDb,
                Band7 = (float)bands[7].GainDb,
                Band8 = (float)bands[8].GainDb,
                Band9 = (float)bands[9].GainDb,
                Q0 = (float)EqParameters.NormalizeQ(bands[0].Q),
                Q1 = (float)EqParameters.NormalizeQ(bands[1].Q),
                Q2 = (float)EqParameters.NormalizeQ(bands[2].Q),
                Q3 = (float)EqParameters.NormalizeQ(bands[3].Q),
                Q4 = (float)EqParameters.NormalizeQ(bands[4].Q),
                Q5 = (float)EqParameters.NormalizeQ(bands[5].Q),
                Q6 = (float)EqParameters.NormalizeQ(bands[6].Q),
                Q7 = (float)EqParameters.NormalizeQ(bands[7].Q),
                Q8 = (float)EqParameters.NormalizeQ(bands[8].Q),
                Q9 = (float)EqParameters.NormalizeQ(bands[9].Q),

            };
        }

        public async Task SetMusicUrl(string musicUrl)
        {
            var req = new SetMusicUrlRequest { Url = musicUrl };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.SetMusicUrlRequestSize);
            try
            {
                int len = BinarySerializer.WriteSetMusicUrlRequest(buf, req);
                await SendCommandAsync(CommandId.SetMusicUrl, new ReadOnlyMemory<byte>(buf, 0, len));
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        /// <summary>
        /// Returns null when the round-trip fails, so callers can keep the last known
        /// value instead of storing a bogus (0, 0).
        /// </summary>
        public async Task<(long currentMs, long totalMs)?> GetTimeProgress()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BinarySerializer.TimeProgressSize);
            try
            {
                var (type, length) = await SendWithResponseAsync(CommandId.GetTimeProgress,
                    ReadOnlyMemory<byte>.Empty, buffer, timeoutMs: 300, skipIfBusy: true);
                return type == MessageTypeId.TimeProgress && length == BinarySerializer.TimeProgressSize
                    ? BinarySerializer.ReadTimeProgress(buffer) : null;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public void SetPosition(long positionMs)
        {
            var req = new ChangePositionRequest { PositionMs = positionMs };
            Span<byte> buf = stackalloc byte[BinarySerializer.ChangePositionRequestSize];
            BinarySerializer.WriteChangePositionRequest(buf, req);
            Publish(CommandId.ChangePosition, buf);
        }

        public void ChangeVolume(double volume)
        {
            var req = new ChangeVolumeRequest { Volume = volume };
            Span<byte> buf = stackalloc byte[BinarySerializer.ChangeVolumeRequestSize];
            BinarySerializer.WriteChangeVolumeRequest(buf, req);
            Publish(CommandId.ChangeVolume, buf);
        }

        public void MusicEnd()
        {
            Publish(CommandId.MusicEnd, []);
        }

        public void FadeOut()
        {
            Publish(CommandId.FadeOut, []);
        }

        // ──────────────── Device enumeration ────────────────

        private async Task<List<(int id, string name)>> GetDevicesPaged(CommandId commandId, MessageTypeId expectedResponse)
        {
            var result = new List<(int, string)>();
            byte page = 0;
            var respBuf = ArrayPool<byte>.Shared.Rent(IpcConstants.MaxResponseSize);
            try
            {
                while (true)
                {
                    var reqBuf = ArrayPool<byte>.Shared.Rent(BinarySerializer.GetDevicesRequestSize);
                    try
                    {
                        var req = new GetDevicesRequest { Page = page };
                        BinarySerializer.WriteGetDevicesRequest(reqBuf, req);
                        var (resType, respLen) = await SendWithResponseAsync(commandId,
                            new ReadOnlyMemory<byte>(reqBuf, 0, BinarySerializer.GetDevicesRequestSize),
                            respBuf, timeoutMs: 5000);
                        if (resType != expectedResponse) break;

                        var resp = respBuf.AsSpan(0, respLen);
                        var (rPage, totalPages, count) = BinarySerializer.ReadDeviceListPageHeader(resp);
                        int off = BinarySerializer.DeviceListPageHeaderSize;
                        for (int i = 0; i < count; i++)
                        {
                            var (id, name, bytesRead) = BinarySerializer.ReadDeviceEntry(resp[off..]);
                            if (bytesRead <= 0) break;
                            result.Add((id, name));
                            off += bytesRead;
                            if (expectedResponse == MessageTypeId.WasapiDevices)
                            {
                                var (_, endpoint, identityBytes) = BinarySerializer.ReadDeviceEntry(resp[off..]);
                                if (identityBytes <= 0) throw new InvalidOperationException("Missing endpoint identity");
                                _wasapiEndpoints[id] = endpoint;
                                off += identityBytes;
                            }
                        }
                        page++;
                        if (page >= totalPages) break;
                    }
                    finally { ArrayPool<byte>.Shared.Return(reqBuf); }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(respBuf); }
            return result;
        }

        public Task<List<(int id, string name)>> GetWasapiDevices()
            => GetDevicesPaged(CommandId.GetWasapiDevices, MessageTypeId.WasapiDevices);

        public Task<List<(int id, string name)>> GetAsioDevices()
            => GetDevicesPaged(CommandId.GetAsioDevices, MessageTypeId.AsioDevices);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _notificationCts?.Cancel();
            _serverMonitorCts?.Cancel();
            _transport?.Dispose();
            _notificationListenerTask?.GetAwaiter().GetResult();
            _serverMonitorTask?.GetAwaiter().GetResult();
            _notificationCts?.Dispose();
            _serverMonitorCts?.Dispose();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
            _notificationReadySemaphore?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
