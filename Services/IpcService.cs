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
        private readonly ILogger<IpcService> _logger;
        private AppViewModel AppViewModel { get; }

        private readonly byte[] _responseBuffer = new byte[IpcConstants.MaxResponseSize];
        private readonly byte[] _timeProgressBuf = new byte[BinarySerializer.TimeProgressSize];
        private readonly byte[] _notificationBuffer = new byte[IpcConstants.MaxNotificationSize];

        public event Action<MessageTypeId, ReadOnlyMemory<byte>>? NotificationReceived;

        public IpcService(AppViewModel appViewModel, ILogger<IpcService> logger)
        {
            AppViewModel = appViewModel;
            _logger = logger;
        }

        public async Task InitializingAsync()
        {
            for (int i = 0; i < 50; i++)
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
                    return;
                }
                catch
                {
                    await Task.Delay(100);
                }
            }
            _logger.LogError("IPC connection failed after retries");
            _isConnected = false;
        }

        public async Task InitializeMusic(Music? music)
        {
            if (music is not null)
                await SetMusicUrl(music.Path);
            await UpdateEq();
            UpdateSettings();
        }

        private void StartNotificationListener()
        {
            _notificationCts = new CancellationTokenSource();
            _notificationListenerTask = Task.Run(() => ListenForNotificationsAsync(_notificationCts.Token));
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
                    _lastNotificationVersion = version;

                    // Double-buffered: the slot is selected by version parity, so the
                    // server never overwrites the slot we are currently reading.
                    long slot = IpcEnvelope.NotificationSlotOffset(version);
                    var typeId = IpcEnvelope.ReadMessageTypeId(_accessor!, slot);
                    int payloadLen = IpcEnvelope.ReadPayload(
                        _accessor!, slot,
                        _notificationBuffer,
                        IpcConstants.MaxNotificationSize - IpcConstants.EnvelopeHeaderSize);

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
                catch (Exception) { await Task.Delay(500, cancellationToken); }
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

                int lastResponseVersion = _lastResponseVersion;
                bool responded = await WaitForResponseAsync(lastResponseVersion, timeoutMs);
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
        /// Waits until the response version advances beyond <paramref name="lastVersion"/>.
        /// A stale signal only causes a spurious wakeup (version unchanged), so a timed-out
        /// request can never poison subsequent requests.
        /// </summary>
        private async Task<bool> WaitForResponseAsync(int lastVersion, int timeoutMs)
        {
            int step = Math.Min(50, timeoutMs);
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                bool signaled = await Task.Run(() => _responseReadySemaphore!.WaitOne(step));
                if (signaled)
                {
                    int version = IpcEnvelope.ReadVersion(_accessor!, IpcConstants.ResponseVersionOffset);
                    if (version != lastVersion)
                    {
                        _lastResponseVersion = version;
                        return true;
                    }
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

        // ──────────────── Public API ────────────────

        public void Play(string musicUrl)
        {
            var req = new PlayRequest { Url = musicUrl };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.PlayRequestSize);
            int len = BinarySerializer.WritePlayRequest(buf, req);
            _ = FireCommand(CommandId.Play, buf, len);
        }

        public void PlayButton()
        {
            FireCommand(CommandId.PlayButton);
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
            _ = FireCommand(CommandId.UpdateSettings, buf, len);
        }

        public async Task UpdateEq()
        {
            var eq = ConvertDictToUpdateEqRequest(AppSettings.Equalizer);
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.UpdateEqRequestSize);
            try
            {
                BinarySerializer.WriteUpdateEqRequest(buf, eq);
                await SendCommandAsync(CommandId.UpdateEq, new ReadOnlyMemory<byte>(buf, 0, BinarySerializer.UpdateEqRequestSize));
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        private static UpdateEqRequest ConvertDictToUpdateEqRequest(Dictionary<string, double> dict)
        {
            return new UpdateEqRequest
            {
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

        public async Task<(long currentMs, long totalMs)> GetTimeProgress()
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
            return (0, 0);
        }

        public void SetPosition(long positionMs)
        {
            var req = new ChangePositionRequest { PositionMs = positionMs };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.ChangePositionRequestSize);
            BinarySerializer.WriteChangePositionRequest(buf, req);
            _ = FireCommand(CommandId.ChangePosition, buf, BinarySerializer.ChangePositionRequestSize);
        }

        public void ChangeVolume(double volume)
        {
            var req = new ChangeVolumeRequest { Volume = volume };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.ChangeVolumeRequestSize);
            BinarySerializer.WriteChangeVolumeRequest(buf, req);
            _ = FireCommand(CommandId.ChangeVolume, buf, BinarySerializer.ChangeVolumeRequestSize);
        }

        public void MusicEnd()
        {
            FireCommand(CommandId.MusicEnd);
        }

        public void ToggleEqualizer()
        {
            FireCommand(CommandId.ToggleEqualizer);
        }

        public void SetEqualizerGain(byte bandIndex, float gain)
        {
            var req = new SetEqualizerGainRequest { BandIndex = bandIndex, Gain = gain };
            var buf = ArrayPool<byte>.Shared.Rent(BinarySerializer.SetEqualizerGainRequestSize);
            BinarySerializer.WriteSetEqualizerGainRequest(buf, req);
            _ = FireCommand(CommandId.SetEqualizerGain, buf, BinarySerializer.SetEqualizerGainRequestSize);
        }

        public void SetEqualizer()
        {
            FireCommand(CommandId.SetEqualizer);
        }

        public void ClearEqualizer()
        {
            FireCommand(CommandId.ClearEqualizer);
        }

        public void FadeOut()
        {
            FireCommand(CommandId.FadeOut);
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
