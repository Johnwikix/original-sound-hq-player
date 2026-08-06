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

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _isConnected = false;

        // Versioned mailbox state: the shared-memory version ints are the source of
        // truth; the semaphores are only wakeup hints, so timeouts and stale signals
        // can never permanently desynchronize the request/response protocol.
        private int _requestVersionCounter;
        private int _lastResponseVersion;
        private int _lastNotificationVersion;

        private CancellationTokenSource? _notificationCts;
        private Task? _notificationListenerTask;
        private CancellationTokenSource? _serverMonitorCts;
        private Task? _serverMonitorTask;
        private readonly ILogger<IpcService> _logger;
        private AppViewModel AppViewModel { get; }

        private readonly byte[] _responseBuffer = new byte[IpcConstants.MaxResponseSize];
        private readonly byte[] _timeProgressBuf = new byte[BinarySerializer.TimeProgressSize];
        private readonly byte[] _playStateBuf = new byte[BinarySerializer.PlayStateResponseSize];
        private readonly byte[] _eqStateBuf = new byte[BinarySerializer.EqStateResponseSize];
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
                    _isConnected = true;
                    StartNotificationListener();
                    StartServerMonitor();
                    return;
                }
                catch
                {
                    await Task.Delay(100);
                }
            }
            _logger.LogCritical("IPC connection failed after retries - core process unavailable, exiting.");
            ShutdownApp();
            _isConnected = false;
        }

        public async Task InitializeMusic(Music? music)
        {
            if (music is not null)
                await SetMusicUrl(music.Path);
            UpdateEq();
            UpdateSettings();
        }

        private void StartNotificationListener()
        {
            _notificationCts = new CancellationTokenSource();
            _notificationListenerTask = Task.Run(() => ListenForNotificationsAsync(_notificationCts.Token));
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

        private async Task ListenForNotificationsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool hasNotification = await Task.Run(() =>
                        _notificationReadySemaphore!.WaitOne(1000), cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!hasNotification) continue;

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
                    try { await Task.Delay(500, cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // ──────────────── Core send ────────────────

        public async Task<MessageTypeId> SendCommandAsync(CommandId commandId, ReadOnlyMemory<byte> payload)
        {
            return (await SendWithResponseAsync(commandId, payload, Array.Empty<byte>())).Type;
        }

        public async Task<(MessageTypeId Type, int ResponseLen)> SendWithResponseAsync(
            CommandId commandId, ReadOnlyMemory<byte> payload, byte[] responseBuffer,
            int timeoutMs = 1000, bool skipIfBusy = false)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("IPC not connected");
                return (MessageTypeId.Failed, 0);
            }

            if (skipIfBusy)
            {
                if (!await _sendLock.WaitAsync(0)) return (MessageTypeId.Failed, 0);
            }
            else
            {
                await _sendLock.WaitAsync();
            }

            try
            {
                int requestVersion = ++_requestVersionCounter;
                IpcEnvelope.WriteCommand(_accessor!, IpcConstants.RequestBufferOffset, commandId, (byte)requestVersion, payload.Span);
                IpcEnvelope.PublishVersion(_accessor!, IpcConstants.RequestVersionOffset, requestVersion);
                try { _requestReadySemaphore!.Release(); }
                catch (SemaphoreFullException) { }

                bool responded = await WaitForResponseAsync((byte)requestVersion, timeoutMs);
                if (!responded) return (MessageTypeId.Failed, 0);

                var typeId = IpcEnvelope.ReadMessageTypeId(_accessor!, IpcConstants.ResponseBufferOffset);
                int respLen = IpcEnvelope.ReadPayload(
                    _accessor!, IpcConstants.ResponseBufferOffset,
                    _responseBuffer,
                    IpcConstants.MaxResponseSize - IpcConstants.EnvelopeHeaderSize);

                if (respLen > 0 && responseBuffer.Length >= respLen)
                    _responseBuffer.AsSpan(0, respLen).CopyTo(responseBuffer);
                return (typeId, respLen);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendCommandAsync error");
                return (MessageTypeId.Failed, 0);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Waits until a response with the expected sequence id arrives. The server echoes
        /// the request's sequence id in the response envelope, so a late response to a
        /// previously timed-out request is recognized and skipped - the protocol can
        /// never get permanently desynchronized by a timeout.
        /// </summary>
        private async Task<bool> WaitForResponseAsync(byte expectedSeq, int timeoutMs)
        {
            int step = Math.Min(50, timeoutMs);
            int lastVersion = _lastResponseVersion;
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                bool signaled = await Task.Run(() => _responseReadySemaphore!.WaitOne(step));
                if (signaled)
                {
                    int version = IpcEnvelope.ReadVersion(_accessor!, IpcConstants.ResponseVersionOffset);
                    if (version == lastVersion) continue;
                    lastVersion = version;
                    _lastResponseVersion = version;

                    byte respSeq = IpcEnvelope.ReadSequenceId(_accessor!, IpcConstants.ResponseBufferOffset);
                    if (respSeq == expectedSeq) return true;
                }
                elapsed += step;
            }
            return false;
        }

        /// <summary>Fire-and-forget: sends command, returns rented buffer to pool after completion.</summary>
        private async Task FireCommand(CommandId commandId, byte[] pooledBuf, int len)
        {
            try
            {
                await SendCommandAsync(commandId, new ReadOnlyMemory<byte>(pooledBuf, 0, len));
            }
            finally
            {
                if (pooledBuf.Length > 0)
                    ArrayPool<byte>.Shared.Return(pooledBuf);
            }
        }

        private void FireCommand(CommandId commandId)
        {
            _ = SendCommandAsync(commandId, ReadOnlyMemory<byte>.Empty);
        }

        // ──────────────── Send-only path ────────────────

        /// <summary>
        /// Publishes a command without waiting for a response. Still serialized through
        /// <see cref="_sendLock"/> so the shared request slot always contains exactly one
        /// unread command - commands are never overwritten, only delayed while the server
        /// processes a long-running request. The server responds to every command; a
        /// fire-and-forget response is simply skipped by the seq matching of a later wait.
        /// </summary>
        private async Task SendOnly(CommandId commandId, ReadOnlyMemory<byte> payload)
        {
            if (!_isConnected) return;
            await _sendLock.WaitAsync();
            try
            {
                int requestVersion = ++_requestVersionCounter;
                IpcEnvelope.WriteCommand(_accessor!, IpcConstants.RequestBufferOffset, commandId, (byte)requestVersion, payload.Span);
                IpcEnvelope.PublishVersion(_accessor!, IpcConstants.RequestVersionOffset, requestVersion);
                try { _requestReadySemaphore!.Release(); }
                catch (SemaphoreFullException) { }
            }
            finally { _sendLock.Release(); }
        }

        private async Task SendOnly(CommandId commandId, byte[] pooledBuf, int len)
        {
            try { await SendOnly(commandId, new ReadOnlyMemory<byte>(pooledBuf, 0, len)); }
            finally { if (pooledBuf.Length > 0) ArrayPool<byte>.Shared.Return(pooledBuf); }
        }

        private void SendOnly(CommandId commandId)
        {
            _ = SendOnly(commandId, ReadOnlyMemory<byte>.Empty);
        }

        // ──────────────── Public API ────────────────

        public void Play(string musicUrl)
        {
            var req = new PlayRequest { Url = musicUrl };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.PlayRequestSize);
            int len = BinarySerializer.WritePlayRequest(buf, req);
            _ = SendOnly(CommandId.Play, buf, len);
        }

        /// <summary>
        /// Toggles play/pause. The response carries the authoritative playback state
        /// (the server echoes it after the state change completes); returns null when
        /// the round-trip fails, in which case the UI is corrected by notifications.
        /// </summary>
        public async Task<bool?> PlayButton()
        {
            var (resType, _) = await SendWithResponseAsync(CommandId.PlayButton, ReadOnlyMemory<byte>.Empty, _playStateBuf);
            return resType == MessageTypeId.PlayState
                ? BinarySerializer.ReadPlayStateResponse(_playStateBuf).IsPlaying
                : null;
        }

        public void UpdateSettings(bool isSettingChanged = false)
        {
            var settings = new IpcSetting
            {
                OutputMode = AppSettings.OutputMode,
                BassOutputDeviceId = AppSettings.BassOutputDeviceId,
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
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.IpcSettingSize);
            int len = BinarySerializer.WriteIpcSetting(buf, settings);
            _ = SendOnly(CommandId.UpdateSettings, buf, len);
        }

        /// <summary>Fire-and-forget full equalizer state sync (slider drags, startup).</summary>
        public void UpdateEq()
        {
            var req = ConvertDictToUpdateEqRequest(AppSettings.Equalizer);
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.UpdateEqRequestSize);
            BinarySerializer.WriteUpdateEqRequest(buf, req);
            _ = SendOnly(CommandId.UpdateEq, buf, BinarySerializer.UpdateEqRequestSize);
        }

        /// <summary>
        /// Sends the full equalizer state and awaits the server's real applied state.
        /// IsEnabled comes back false when the output mode rejects the EQ (e.g. DSD over
        /// exclusive output); callers should roll the UI switch back in that case.
        /// Returns null when the round-trip fails.
        /// </summary>
        public async Task<bool?> UpdateEqAsync()
        {
            var req = ConvertDictToUpdateEqRequest(AppSettings.Equalizer);
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.UpdateEqRequestSize);
            try
            {
                BinarySerializer.WriteUpdateEqRequest(buf, req);
                var (resType, _) = await SendWithResponseAsync(CommandId.UpdateEq,
                    new ReadOnlyMemory<byte>(buf, 0, BinarySerializer.UpdateEqRequestSize),
                    _eqStateBuf, timeoutMs: 1000);
                return resType == MessageTypeId.EqState
                    ? BinarySerializer.ReadEqStateResponse(_eqStateBuf).IsEnabled
                    : null;
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        private static UpdateEqRequest ConvertDictToUpdateEqRequest(Dictionary<string, double> dict)
        {
            return new UpdateEqRequest
            {
                IsEnabled = AppSettings.IsEqualizerEnabled,
                Band0 = (float)(dict.TryGetValue("32Hz", out var v) ? v : 0),
                Band1 = (float)(dict.TryGetValue("64Hz", out v) ? v : 0),
                Band2 = (float)(dict.TryGetValue("125Hz", out v) ? v : 0),
                Band3 = (float)(dict.TryGetValue("250Hz", out v) ? v : 0),
                Band4 = (float)(dict.TryGetValue("500Hz", out v) ? v : 0),
                Band5 = (float)(dict.TryGetValue("1kHz", out v) ? v : 0),
                Band6 = (float)(dict.TryGetValue("2kHz", out v) ? v : 0),
                Band7 = (float)(dict.TryGetValue("4kHz", out v) ? v : 0),
                Band8 = (float)(dict.TryGetValue("8kHz", out v) ? v : 0),
                Band9 = (float)(dict.TryGetValue("16kHz", out v) ? v : 0),
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
            // skipIfBusy: drop this tick if the previous progress request is still in
            // flight, so a slow server cannot queue up stale progress polls.
            var (resType, _) = await SendWithResponseAsync(CommandId.GetTimeProgress,
                ReadOnlyMemory<byte>.Empty, _timeProgressBuf, timeoutMs: 300, skipIfBusy: true);
            if (resType == MessageTypeId.TimeProgress)
            {
                var (curMs, totalMs) = BinarySerializer.ReadTimeProgress(_timeProgressBuf);
                return (curMs, totalMs);
            }
            return null;
        }

        public void SetPosition(long positionMs)
        {
            var req = new ChangePositionRequest { PositionMs = positionMs };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.ChangePositionRequestSize);
            BinarySerializer.WriteChangePositionRequest(buf, req);
            _ = SendOnly(CommandId.ChangePosition, buf, BinarySerializer.ChangePositionRequestSize);
        }

        public void ChangeVolume(double volume)
        {
            var req = new ChangeVolumeRequest { Volume = volume };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.ChangeVolumeRequestSize);
            BinarySerializer.WriteChangeVolumeRequest(buf, req);
            _ = SendOnly(CommandId.ChangeVolume, buf, BinarySerializer.ChangeVolumeRequestSize);
        }

        public void MusicEnd()
        {
            SendOnly(CommandId.MusicEnd);
        }

        public void FadeOut()
        {
            SendOnly(CommandId.FadeOut);
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
            _notificationCts?.Cancel();
            _serverMonitorCts?.Cancel();
            _accessor?.Dispose();
            _mmf?.Dispose();
            _requestReadySemaphore?.Dispose();
            _responseReadySemaphore?.Dispose();
            _sendLock?.Dispose();
            _isConnected = false;
            GC.SuppressFinalize(this);
        }
    }
}
