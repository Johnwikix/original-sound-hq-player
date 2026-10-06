using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public partial class IpcService : IDisposable
    {
        private readonly object _lifecycleGate = new();
        private readonly CancellationTokenSource _connectionStop = new();
        private Task? _initializationTask;
        private PipeStateClient? _stateClient;
        private long _nextSeekId;
        private long _progressSequence;
        private Func<ProgressSnapshot?>? _remoteProgress;
        public void AttachRemoteProgress(Func<ProgressSnapshot?> source) => _remoteProgress = source;
        private readonly object _seekPublishGate = new();
        private DspStateSnapshot? _dspSnapshot;
        private FftSnapshot? _fftSnapshot;
        // Placeholder request state for the future spectrum effect. It is not user-facing.
        private int _fftRequested;

        /// <summary>连接内最新完整 DSP 状态；不可变快照可跨线程读取。</summary>
        public DspStateSnapshot? CurrentDspState => Volatile.Read(ref _dspSnapshot);
        /// <summary>在监听线程触发；UI 订阅者必须调度到 DispatcherQueue。</summary>
        public event Action? DspStateChanged;

        /// <summary>Latest FFT snapshot. Subscribers run on the state-pipe reader thread.</summary>
        public FftSnapshot? CurrentFftSnapshot => Volatile.Read(ref _fftSnapshot);
        public event Action<FftSnapshot>? FftDataChanged;
        public bool FftRequested => Volatile.Read(ref _fftRequested) != 0;

        private readonly Dictionary<int, string> _wasapiEndpoints = new();
        private readonly Dictionary<int, string> _asioEndpoints = new();
        public string? GetAsioEndpointId(int id) => _asioEndpoints.GetValueOrDefault(id);
        /// <summary>获取本次枚举中设备索引对应的稳定端点 ID。</summary>
        public string? GetWasapiEndpointId(int id) => _wasapiEndpoints.GetValueOrDefault(id);
        private PipeCommandClient? _transport;
        private int _connected;
        private int _disposed;
        private int _faulted;

        /// <summary>命令与状态管道属于同一播放进程；断开时按既有策略整体退出。</summary>
        public bool IsConnected => Volatile.Read(ref _connected) == 1;

        private readonly ILogger<IpcService> _logger;
        private readonly LicenseService _license;
        private readonly NotificationService _systemNotifications;
        private bool _lastLicenseRestricted;
        private AppViewModel AppViewModel { get; }

        /// <summary>
        /// Raised on the notification listener thread when a notification arrives.
        /// Contract: the <see cref="ReadOnlyMemory{T}"/> payload points into a
        /// reused pipe buffer and is only valid DURING the handler invocation -
        /// handlers must copy (or parse synchronously) before returning.
        /// </summary>
        public event Action<MessageTypeId, ReadOnlyMemory<byte>>? NotificationReceived;

        public IpcService(AppViewModel appViewModel, LicenseService license, ILogger<IpcService> logger, NotificationService systemNotifications)
        {
            AppViewModel = appViewModel;
            _systemNotifications = systemNotifications;
            _license = license;
            _lastLicenseRestricted = license.IsRestricted;
            _logger = logger;
            _license.StateChanged += OnLicenseStateChanged;
        }

        public Task InitializingAsync()
        {
            lock (_lifecycleGate)
            {
                if (_disposed != 0) return Task.CompletedTask;
                return _initializationTask ??= ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            PipeCommandClient? commands = null;
            PipeStateClient? state = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_connectionStop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                commands = await PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, timeout.Token).ConfigureAwait(false);
                state = await PipeStateClient.ConnectAsync(IpcConstants.StatePipeName, timeout.Token).ConfigureAwait(false);
                if (commands.InstanceId != state.InstanceId) throw new InvalidOperationException("Audio pipe instance mismatch.");
                lock (_lifecycleGate)
                {
                    if (_disposed != 0) return;
                    _transport = commands;
                    _stateClient = state;
                    commands.CommandFailed += OnCommandFailed;
                    commands.Faulted += OnTransportFault;
                    state.Faulted += OnTransportFault;
                    state.DspStateChanged += OnDspState;
                    state.FftDataChanged += OnFftData;
                    state.NotificationReceived += OnNotification;
                    Volatile.Write(ref _connected, 1);
                    // A taskbar visualizer may already be active when the audio
                    // process is restarted. Replay the in-memory feature request
                    // after reconnect so it does not silently lose its FFT stream.
                    if (Volatile.Read(ref _fftRequested) != 0)
                    {
                        byte[] fftBuffer = new byte[BinarySerializer.FftEnabledSize];
                        BinarySerializer.WriteFftEnabled(fftBuffer, enabled: true);
                        commands.Publish(CommandId.SetFftEnabled, fftBuffer, coalesce: true);
                    }
                    state.Start();
                    commands = null;
                    state = null;
                }
            }
            catch (OperationCanceledException) when (_connectionStop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Audio pipe connection failed; exiting.");
                QueueShutdown();
                throw;
            }
            finally
            {
                if (state is not null) await state.DisposeAsync().ConfigureAwait(false);
                if (commands is not null) await commands.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void OnCommandFailed(CommandId command) => _logger.LogWarning("Audio command {Command} was rejected", command);

        private void OnTransportFault(Exception exception)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _faulted, 1) != 0) return;
            Volatile.Write(ref _connected, 0);
            Volatile.Write(ref _dspSnapshot, null);
            Volatile.Write(ref _fftSnapshot, null);
            _logger.LogError(exception, "Audio pipe disconnected; exiting.");
            NotifyDspStateChanged();
            QueueShutdown();
        }

        private static void QueueShutdown()
            => ThreadPool.QueueUserWorkItem(static _ => ShutdownApp());

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
            try { await UpdateDeviceCorrectionsAsync(force: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup correction synchronization was not confirmed; initialization will continue");
            }
            if (music is not null && !music.IsRemote)
                await SetMusicUrl(music.Path);
            UpdateEq();
            UpdateSettings();
            UpdateDsp();
        }

        private void NotifyDspStateChanged()
        {
            if (DspStateChanged is not { } handlers) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { _logger.LogWarning(ex, "DSP state subscriber failed"); }
            }
        }

        private void OnDspState(DspStateSnapshot snapshot)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0 || snapshot.Revision <= (CurrentDspState?.Revision ?? 0)) return;
            var previous = CurrentDspState?.State;
            Volatile.Write(ref _dspSnapshot, snapshot);
            try
            {
                SynchronizeCorrectionOutput(previous, snapshot.State);
                SynchronizeAtmosState(snapshot);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DSP state synchronization failed"); }
            NotifyDspStateChanged();
        }

        private void OnFftData(FftSnapshot snapshot)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
                || snapshot.Sequence <= (CurrentFftSnapshot?.Sequence ?? 0)) return;
            Volatile.Write(ref _fftSnapshot, snapshot);
            if (FftDataChanged is not { } handlers) return;
            foreach (Action<FftSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(snapshot); }
                catch (Exception ex) { _logger.LogWarning(ex, "FFT data subscriber failed"); }
            }
        }

        private void OnNotification(MessageTypeId type, ReadOnlyMemory<byte> payload)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0 || NotificationReceived is not { } handlers) return;
            foreach (Action<MessageTypeId, ReadOnlyMemory<byte>> handler in handlers.GetInvocationList())
            {
                try { handler(type, payload); }
                catch (Exception ex) { _logger.LogWarning(ex, "Audio notification subscriber failed"); }
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
            bool coalesce = commandId is CommandId.ChangeVolume or CommandId.UpdateEq or CommandId.UpdateDsp or CommandId.SetFftEnabled;
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
                ExperimentalSurround51 = AppViewModel.ExperimentalSurround51,
                ExperimentalAtmosPassthrough = AppViewModel.ExperimentalAtmosPassthrough,
                AtmosEndpointId = AppViewModel.AtmosEndpointId,
                DsdGain = AppViewModel.DsdGain,
                DsdPcmFreq = AppViewModel.DsdPcmFreq,
                IsEqualizerEnabled = AppSettings.IsEqualizerEnabled,
                Volume = (float)(App.Services.GetRequiredService<AppViewModel>().Volume / 100.0),
                IsSettingChanged = isSettingChanged,
                IsFadeEnabled = AppViewModel.IsFadeEnabled,
            };
            // 受限时关闭高级输出，沿用引擎的保进度重建及 PCM/立体声回退。
            _license.ApplyOutputRestrictions(ref settings);
            Span<byte> buf = stackalloc byte[BinarySerializer.IpcSettingSize];
            int len = BinarySerializer.WriteIpcSetting(buf, settings);
            Publish(CommandId.UpdateSettings, buf[..len]);
        }

        /// <summary>完整绑定集合随有序命令发送，执行确认后才更新已应用快照。</summary>
        private readonly SemaphoreSlim _correctionPublishGate = new(1, 1);
        private DeviceCorrections? _appliedCorrections;
        private readonly CancellationTokenSource _correctionRetryCts = new();
        /// <summary>校正同步未确认；设置页可在启动失败后继续重试。</summary>
        public bool CorrectionSyncFailed { get; private set; }
        public event Action? CorrectionSyncChanged;

        private void SetCorrectionSyncFailed(bool failed)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (CorrectionSyncFailed == failed) return;
            CorrectionSyncFailed = failed;
            if (CorrectionSyncChanged is not { } handlers) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Correction sync subscriber failed"); }
            }
        }

        /// <summary>主页初始化后有限重试，每次发送最新配置；退出取消等待。</summary>
        public async Task RetryStartupCorrectionsAsync()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var token = _correctionRetryCts.Token;
            try
            {
                for (int attempt = 1; attempt <= 2 && CorrectionSyncFailed; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                    if (!CorrectionSyncFailed || Volatile.Read(ref _disposed) != 0) return;
                    try { await UpdateDeviceCorrectionsAsync(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Correction synchronization retry {Attempt} was not confirmed", attempt); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public async Task UpdateDeviceCorrectionsAsync(bool force = false)
        {
            await _correctionPublishGate.WaitAsync();
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                // 在串行入口内取最新快照，避免排队期间旧配置覆盖新配置。
                var snapshot = AppSettings.DeviceCorrections;
                if (!force && ReferenceEquals(snapshot, _appliedCorrections)) return;
                byte[] payload = await Task.Run(() => DeviceCorrectionProtocol.Write(snapshot));
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var result = await SendCommandAsync(CommandId.UpdateDeviceCorrections, payload);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (result != MessageTypeId.Success) throw new InvalidOperationException("Audio correction acknowledgement failed.");
                _appliedCorrections = snapshot;
                PublishLiveCorrection();
                SetCorrectionSyncFailed(false);
            }
            catch
            {
                // 确认失败时播放端状态未知，不能再用旧的成功快照跳过重发。
                _appliedCorrections = null;
                SetCorrectionSyncFailed(true);
                throw;
            }
            finally { _correctionPublishGate.Release(); }
        }

        /// <summary>发送音效快照，不触发输出设备重建。</summary>
        public void UpdateDsp()
        {
            Span<byte> buffer = stackalloc byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(buffer, LicensePolicy.ApplyDspRestrictions(AppSettings.Dsp, _license.RestrictedFeatures));
            Publish(CommandId.UpdateDsp, buffer);
            PublishLiveCorrection();
        }

        /// <summary>Feature hook for a future visualizer; changing it never rebuilds audio output.</summary>
        public void SetFftEnabled(bool enabled)
        {
            Volatile.Write(ref _fftRequested, enabled ? 1 : 0);
            Span<byte> buffer = stackalloc byte[BinarySerializer.FftEnabledSize];
            BinarySerializer.WriteFftEnabled(buffer, enabled);
            Publish(CommandId.SetFftEnabled, buffer);
        }

        /// <summary>许可门控变化时重推输出与音效设置；剩余天数变化不触发重推：
        /// 受限瞬间 DSD 位流会话当场回退 PCM，购买后原设置即时恢复。</summary>
        private void OnLicenseStateChanged()
        {
            // 启动查询与设置加载并行，首推由 InitializeMusic 统一发布完整设置。
            if (!AppViewModel.IsInitialized) { _lastLicenseRestricted = _license.IsRestricted; return; }
            if (Volatile.Read(ref _disposed) != 0 || _transport == null) return;
            bool restricted = _license.IsRestricted;
            if (_lastLicenseRestricted == restricted) return;
            try
            {
                UpdateSettings();
                UpdateDsp();
                _lastLicenseRestricted = restricted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "许可状态变化后的设置重推失败");
            }
        }

        /// <summary>输出代数限定的实时校正，仅发送到播放端，不覆盖设备保存的绑定。</summary>
        private long _previewSequence = DateTime.UtcNow.Ticks;
        public void PreviewDsp(DspSettings settings, string deviceId, long generation)
        {
            if (_license.IsFeatureRestricted(LicenseFeature.Convolution)) return;
            Span<byte> buffer = stackalloc byte[DspPreview.Size];
            new DspPreview(Interlocked.Increment(ref _previewSequence), generation, deviceId, false,
                LicensePolicy.ApplyDspRestrictions(settings, _license.RestrictedFeatures)).Write(buffer);
            Publish(CommandId.PreviewDsp, buffer);
        }

        public void EndDspPreview()
        {
            Span<byte> buffer = stackalloc byte[DspPreview.Size];
            new DspPreview(Interlocked.Increment(ref _previewSequence), 0, "", true, new()).Write(buffer);
            Publish(CommandId.PreviewDsp, buffer);
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
        /// Reads latest telemetry without queuing a request. Legacy server command remains for tools.
        /// </summary>
        public Task<(long currentMs, long totalMs)?> GetTimeProgress()
        {
            (long, long)? value = TryGetProgressSnapshot(out var snapshot) ? (snapshot.CurrentMs, snapshot.TotalMs) : null;
            return Task.FromResult(value);
        }

        public bool TryGetProgressSnapshot(out ProgressSnapshot snapshot)
        {
            snapshot = default;
            var remote = _remoteProgress?.Invoke();
            if (remote is not null) snapshot = remote.Value;
            else if (_stateClient?.TryGetProgress(out snapshot) != true) return false;
            snapshot = snapshot with { Revision = Interlocked.Increment(ref _progressSequence) };
            return true;
        }

        public void SetPosition(long positionMs)
        {
            lock (_seekPublishGate)
            {
                long seekId = Interlocked.Increment(ref _nextSeekId);
                AppViewModel.BeginProgressSeek(positionMs, seekId);
                var req = new ChangePositionRequest { PositionMs = positionMs, SeekId = seekId };
                Span<byte> buf = stackalloc byte[BinarySerializer.ChangePositionRequestSize];
                BinarySerializer.WriteChangePositionRequest(buf, req);
                if (_transport?.Publish(CommandId.ChangePosition, buf, coalesce: false) != true)
                {
                    AppViewModel.CancelProgressSeek(seekId);
                    _logger.LogWarning("Audio seek could not be queued");
                }
            }
        }

        public void ChangeVolume(double volume)
        {
            var req = new ChangeVolumeRequest { Volume = volume };
            Span<byte> buf = stackalloc byte[BinarySerializer.ChangeVolumeRequestSize];
            BinarySerializer.WriteChangeVolumeRequest(buf, req);
            Publish(CommandId.ChangeVolume, buf);
        }

        public async Task<bool> QueueNextAsync(GaplessRequest request)
        {
            byte[] reply = new byte[16];
            var (type, length) = await SendWithResponseAsync(CommandId.QueueNext, request.Write(), reply);
            return type == MessageTypeId.Success && length == reply.Length
                && System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(8)) == request.Epoch;
        }

        /// <summary>Cancellation may race a render boundary. Return the committed identity so the UI
        /// can still show a track that was already audible before cancellation reached the engine.</summary>
        public async Task<long> CancelQueuedNextAsync()
        {
            byte[] reply = new byte[16];
            var (type, length) = await SendWithResponseAsync(CommandId.QueueNext, new GaplessRequest(0, 0, "").Write(), reply);
            if (type != MessageTypeId.Success || length != reply.Length)
                throw new InvalidOperationException("Next-track cancellation was not confirmed.");
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(reply);
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
                            if (expectedResponse is MessageTypeId.WasapiDevices or MessageTypeId.AsioDevices)
                            {
                                var (_, endpoint, identityBytes) = BinarySerializer.ReadDeviceEntry(resp[off..]);
                                if (identityBytes <= 0) throw new InvalidOperationException("Missing endpoint identity");
                                (expectedResponse == MessageTypeId.WasapiDevices ? _wasapiEndpoints : _asioEndpoints)[id] = endpoint;
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
            Task? initialization;
            lock (_lifecycleGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Volatile.Write(ref _connected, 0);
                _connectionStop.Cancel();
                initialization = _initializationTask;
            }
            _license.StateChanged -= OnLicenseStateChanged;
            _correctionRetryCts.Cancel();
            try { initialization?.GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Audio initialization ended during shutdown"); }
            if (_stateClient is not null)
            {
                _stateClient.Faulted -= OnTransportFault;
                _stateClient.DspStateChanged -= OnDspState;
                _stateClient.FftDataChanged -= OnFftData;
                _stateClient.NotificationReceived -= OnNotification;
                try { _stateClient.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Audio state pipe cleanup failed"); }
            }
            if (_transport is not null)
            {
                _transport.Faulted -= OnTransportFault;
                _transport.CommandFailed -= OnCommandFailed;
                try { _transport.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Audio command pipe cleanup failed"); }
            }
            _correctionRetryCts.Dispose();
            _connectionStop.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
