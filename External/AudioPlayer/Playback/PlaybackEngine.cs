using System.Diagnostics;
using AudioPlayer.Interop;
using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>
/// 播放引擎：BassPlayerSharp PlayBackService 的全功能替代（IPC 面逐一对应）。
/// 输出模式串与 bass IPC 完全一致：
/// "DirectSound" / "WasapiShared" / "WasapiExclusivePush" / "WasapiExclusiveEvent" / "ASIO"。
/// 行为对等要点：
/// - 暂停=冻结（共享/独占 IAudioClient::Stop、ASIO ASIOStop，环内数据保留续播）；
/// - 换设备/模式（IsSettingChanged）保进度重建会话；
/// - EQ 在 DoP/NativeDSD 位流下拒绝（EqState 回滚语义）；
/// - 自然结束只发 PlayEnded（不发 PlayState，与 bass SyncFlags.End 一致）；
/// - 进度为采样精确（anchor + played），非旧的字节比例估算。
/// 新语义（按决策）：淡入淡出全 PCM 模式生效、采样精确斜坡；.wv-DSD 走 PCM 解码。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly PlayerIpcService _ipc;

    private readonly object _streamLock = new();
    private Session? _session;
    private IAudioOutput? _output;
    private Timer? _endedWatchdog;
    private int _fadeBusy; // Interlocked：换曲淡出进行中
    private volatile bool _pauseFadeActive; // 暂停淡出进行中（音量同步让路）
    private int _pauseFadeToken; // 暂停淡出令牌：期间再次按下播放则取消本次停机

    public bool IsPlaying;
    public string OutputMode = "DirectSound";
    public int BassOutputDeviceId = -1;
    public int BassASIODeviceId = 0;
    public int Latency = 300;
    public bool IsDopEnabled;
    public string? MusicUrl;
    public int DsdGain = 6;
    public int DsdPcmFreq = 88200;
    public bool IsEqualizerEnabled;
    public bool IsFadingEnabled;
    public float Volume = 0.5f;

    public readonly float[] EqGains = new float[10];

    private static readonly string DsfExtension = ".dsf";
    private static readonly string DffExtension = ".dff";

    public PlaybackEngine(PlayerIpcService ipc)
    {
        _ipc = ipc;
        _endedWatchdog = new Timer(_ => WatchdogTick(), null, 50, 50);
    }

    // ─────────────── 文件类别 ───────────────

    private static bool IsRawDsdContainer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(DsfExtension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(DffExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前曲目是否为位流路径（DoP / NativeDSD）。</summary>
    private bool IsBitstreamActive(string? url) =>
        IsDopEnabled && IsRawDsdContainer(url)
        && (OutputMode.Contains("WasapiExclusive") || OutputMode == "ASIO");

    /// <summary>当前会话实际渲染种类（无会话时按设置预判）。</summary>
    private RenderKind EffectiveKind =>
        _session?.Kind ?? (IsBitstreamActive(MusicUrl)
            ? (OutputMode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop)
            : RenderKind.Pcm);

    // ─────────────── 播放控制（IPC 面） ───────────────

    public void PlayMusic(string musicUrl, bool isSettingChanged = false)
    {
        lock (_streamLock)
        {
            MusicUrl = musicUrl;
            if (IsFadingEnabled && IsPlaying && _session is { Kind: RenderKind.Pcm } && _output != null)
            {
                var (curMs, totalMs) = GetTimeProgress();
                MusicFadeOut(musicUrl, curMs, totalMs);
            }
            else
            {
                SwitchTo(musicUrl);
            }
        }
    }

    private void SwitchTo(string musicUrl)
    {
        DisposeSession();
        _session = OpenSession(musicUrl);
        if (_session != null) StartOutputAndPlay();
    }

    private Session? OpenSession(string url, bool forceSharedFormat = false)
    {
        // 回退共享时强制 PCM：位流会话（DoP/NativeDSD）在共享模式必然失败
        var kind = forceSharedFormat ? RenderKind.Pcm : EffectiveKind;
        // 共享模式（DirectSound/WasapiShared，或独占/ASIO 失败回退）会话直接按端点
        // 混音格式构建：swresample 直出混音率/声道，端点格式精确匹配
        // （虚拟声卡常只接受混音格式，Senary 实测 44.1k 全拒）。
        int? forcedRate = null;
        int? forcedChannels = null;
        if (kind == RenderKind.Pcm && (forceSharedFormat || IsSharedMode(OutputMode)))
        {
            var mix = GetMixFormatCached(IsSharedDeviceIndexed(OutputMode) ? BassOutputDeviceId : -1);
            if (mix != null)
            {
                forcedRate = mix.SampleRate;
                forcedChannels = mix.Channels;
            }
        }
        var session = Session.Open(this, url, kind, DsdPcmFreq, DsdGain, Latency, forcedRate, forcedChannels);
        if (session == null) Console.WriteLine($"[engine] OpenSession failed: kind={kind} url={url}");
        return session;
    }

    /// <summary>淡出后等待端点缓冲排空的余量（渲染领先可闻播放 LatencyMs）。</summary>
    private int FadeDrainMs => Math.Min(1500, (_output?.LatencyMs ?? 0) + 100);

    private static bool IsSharedMode(string mode) => mode is not ("WasapiExclusivePush" or "WasapiExclusiveEvent" or "ASIO");
    private static bool IsSharedDeviceIndexed(string mode) => mode == "WasapiShared";

    private static WasapiDeviceList.SharedMixFormat? _cachedMix;
    private static int _cachedMixDevice = int.MinValue;

    private static WasapiDeviceList.SharedMixFormat? GetMixFormatCached(int deviceIndex)
    {
        if (_cachedMixDevice == deviceIndex && _cachedMix != null) return _cachedMix;
        _cachedMix = WasapiDeviceList.GetSharedMixFormat(deviceIndex);
        _cachedMixDevice = deviceIndex;
        return _cachedMix;
    }

    /// <summary>创建输出并开始播放；首选输出失败回退 WASAPI 共享（对应 bass 回退 DirectSound）。</summary>
    private void StartOutputAndPlay()
    {
        var session = _session!;
        IAudioOutput? output = CreateOutput(session);
        if (output == null && !IsSharedMode(OutputMode))
        {
            // 独占/ASIO 失败 → 回退共享：会话按混音格式重建（率/声道可能与源不同）
            Console.WriteLine("[engine] primary output failed, fallback to shared");
            DisposeSession();
            _session = OpenSession(MusicUrl!, forceSharedFormat: true);
            if (_session == null) { IsPlaying = false; return; }
            output = CreateSharedOutput(_session);
            if (output == null) { IsPlaying = false; return; }
        }
        if (output == null) { IsPlaying = false; return; }
        _output = output;
        ApplyEqToSession();
        ApplyVolumeToOutput();
        if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
        {
            // 淡入（bass 语义：每次起播/换曲都生效）。必须在 ApplyVolumeToOutput 之后：
            // 音量同步会 RampTo(Volume,20)，随后归零并铺 500ms 斜坡。
            // 预缓冲静音段不推进斜坡（见 Session.FillPcm），出声即从 0 起
            _session.Gain.SetImmediately(0f);
            _session.Gain.RampTo(Volume, 500);
        }
        IsPlaying = true;
        _ipc.PlayStateUpdate(IsPlaying);
    }

    private IAudioOutput? CreateOutput(Session session)
    {
        switch (OutputMode)
        {
            case "WasapiExclusivePush":
            case "WasapiExclusiveEvent":
            {
                var output = new WasapiOutput(true, OutputMode == "WasapiExclusivePush");
                if (output.Start(BassOutputDeviceId, Latency, session)) return output;
                output.Dispose();
                return null;
            }
            case "ASIO":
            {
                var output = new AsioOutput();
                if (output.Start(BassASIODeviceId, ExclusiveBufferFrames(session), session)) return output;
                output.Dispose();
                return null;
            }
            default: // DirectSound / WasapiShared → WASAPI 共享
                return CreateSharedOutput(session, OutputMode == "WasapiShared" ? BassOutputDeviceId : -1);
        }
    }

    private static int ExclusiveBufferFrames(Session session) => (int)((long)session.SampleRate * 300 / 8000);

    private IAudioOutput? CreateSharedOutput(Session session, int deviceIndex = -1)
    {
        try
        {
            var output = new WasapiOutput(false, false);
            return output.Start(deviceIndex, Latency, session) ? output : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[engine] shared fallback error: {ex.Message}");
            return null;
        }
    }

    public void PlayButton()
    {
        lock (_streamLock)
        {
            if (IsPlaying)
            {
                if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
                {
                    // 淡出（bass：按下即通知，音频在后台淡完）。渲染领先可闻播放一个端点缓冲，
                    // 必须等斜坡"排空到喇叭"再 Stop，否则尾段被硬裁
                    _session.Gain.RampTo(0f, 500);
                    IsPlaying = false;
                    _ipc.PlayStateUpdate(IsPlaying);
                    int token = ++_pauseFadeToken;
                    _pauseFadeActive = true;
                    Task.Run(async () =>
                    {
                        await Task.Delay(500 + FadeDrainMs);
                        lock (_streamLock)
                        {
                            if (token == _pauseFadeToken && _session != null)
                            {
                                _output?.Pause();
                            }
                            _pauseFadeActive = false;
                        }
                    });
                }
                else
                {
                    _output?.Pause();
                    IsPlaying = false;
                    _ipc.PlayStateUpdate(IsPlaying);
                }
            }
            else
            {
                if (_session == null)
                {
                    if (!string.IsNullOrWhiteSpace(MusicUrl))
                    {
                        SwitchTo(MusicUrl);
                        return;
                    }
                }
                else
                {
                    _output?.Resume();
                    if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
                        _session.Gain.RampTo(Volume, 500);
                }
                IsPlaying = true;
                _ipc.PlayStateUpdate(IsPlaying);
            }
        }
    }

    /// <summary>换曲淡出（bass 规则：剩余&lt;3s 或时长未知跳过；时长 min(剩余/2,500)ms）。</summary>
    private async void MusicFadeOut(string newMusicUrl, long curMs, long totalMs)
    {
        if (Interlocked.CompareExchange(ref _fadeBusy, 1, 0) != 0) return;
        try
        {
            long remainingMs = totalMs - curMs;
            if (remainingMs < 3000 || totalMs <= 0)
            {
                lock (_streamLock) SwitchTo(newMusicUrl);
                return;
            }
            int fadeMs = (int)Math.Min(remainingMs / 2, 500);
            var gain = _session?.Gain;
            int drain = FadeDrainMs;
            gain?.RampTo(0f, fadeMs);
            await Task.Delay(fadeMs + drain);
            lock (_streamLock)
            {
                if (!IsPlaying) return; // 淡出期间用户已暂停：无需再切（下次 Play 会切）
                SwitchTo(newMusicUrl);
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _fadeBusy, 0);
        }
    }

    public void FadeOut()
    {
        lock (_streamLock)
        {
            if (!IsPlaying) return;
            if (_session is { Kind: RenderKind.Pcm, Gain: not null })
            {
                _session.Gain.RampTo(0f, 500);
                IsPlaying = false;
                _ipc.PlayStateUpdate(IsPlaying);
                int token = ++_pauseFadeToken;
                _pauseFadeActive = true;
                Task.Run(async () =>
                {
                    await Task.Delay(500 + FadeDrainMs);
                    lock (_streamLock)
                    {
                        if (token == _pauseFadeToken && _session != null) _output?.Pause();
                        _pauseFadeActive = false;
                    }
                });
            }
            else
            {
                _output?.Pause();
                IsPlaying = false;
                _ipc.PlayStateUpdate(IsPlaying);
            }
        }
    }

    public void MusicEnd()
    {
        lock (_streamLock)
        {
            try
            {
                _output?.Pause();
                // Stop 后可能仍有一个在途渲染周期（已置位的事件 + 整拍缓冲），
                // 等它落地后再复位，否则进度会停在 ~1 拍而非 0
                Thread.Sleep(40);
                _session?.RequestSeek(0);
            }
            catch { }
            IsPlaying = false;
        }
    }

    public void ChangeWaveChannelTime(long positionMs)
    {
        lock (_streamLock)
        {
            try { _session?.RequestSeek(Math.Max(0, positionMs)); }
            catch { }
        }
    }

    public void SetVolume(double volume)
    {
        lock (_streamLock)
        {
            Volume = (float)volume;
            ApplyVolumeToOutput();
        }
    }

    /// <summary>按模式施加音量：共享=会话音量；DirectSound/独占/ASIO(PCM)=软件斜坡；位流=无增益。</summary>
    private void ApplyVolumeToOutput()
    {
        if (_pauseFadeActive) return; // 暂停淡出进行中：音量同步会让淡出斜坡跳回，等暂停完成后再说
        var session = _session;
        var output = _output;
        if (session == null || output == null) return;
        switch (OutputMode)
        {
            case "WasapiShared":
                if (output is WasapiOutput shared) shared.SetSessionVolume(Volume);
                break;
            case "WasapiExclusivePush":
            case "WasapiExclusiveEvent":
            case "ASIO":
                session.Gain?.RampTo(Volume, 20); // PCM 才有 Gain；位流忽略
                break;
            default: // DirectSound
                session.Gain?.RampTo(Volume, 20);
                break;
        }
    }

    public (long currentMs, long totalMs) GetTimeProgress()
    {
        var session = _session;
        if (session == null) return (0, 0);
        long total = session.TotalMs;
        long cur = Math.Min(session.CurrentMs, total > 0 ? total : long.MaxValue);
        return (cur, total);
    }

    public void UpdateSettings(IpcSetting s)
    {
        lock (_streamLock)
        {
            OutputMode = s.OutputMode ?? "DirectSound";
            BassOutputDeviceId = s.BassOutputDeviceId;
            BassASIODeviceId = s.BassASIODeviceId;
            Latency = s.Latency;
            IsDopEnabled = s.IsDopEnabled;
            DsdGain = s.DsdGain;
            DsdPcmFreq = s.DsdPcmFreq;
            Volume = s.Volume;
            IsFadingEnabled = s.IsFadeEnabled;
            if (s.IsSettingChanged) ChangingSetting();
            else ApplyVolumeToOutput(); // 常规设置同步也刷新音量（与 bass UpdateSettings 一致）
        }
    }

    /// <summary>设备/模式热切换：保进度重建会话（bass ChangingSetting 对等）。</summary>
    public void ChangingSetting()
    {
        try
        {
            lock (_streamLock)
            {
                var (curMs, _) = GetTimeProgress();
                bool wasPlaying = IsPlaying;
                DisposeSession();
                var url = MusicUrl;
                if (url == null) return;
                _session = OpenSession(url);
                if (_session != null)
                {
                    _session.RequestSeek(curMs);
                    if (wasPlaying || IsPlaying) StartOutputAndPlay();
                }
                else IsPlaying = false;
            }
        }
        catch { }
    }

    // ─────────────── EQ（IPC 面） ───────────────

    public EqStateResponse SetEqualizerState(UpdateEqRequest req)
    {
        lock (_streamLock)
        {
            EqGains[0] = req.Band0;
            EqGains[1] = req.Band1;
            EqGains[2] = req.Band2;
            EqGains[3] = req.Band3;
            EqGains[4] = req.Band4;
            EqGains[5] = req.Band5;
            EqGains[6] = req.Band6;
            EqGains[7] = req.Band7;
            EqGains[8] = req.Band8;
            EqGains[9] = req.Band9;

            bool requested = req.IsEnabled;
            // 位流模式（DoP/NativeDSD）拒绝 EQ —— 与 bass ToggleEqualizer 拒绝条件对齐
            bool accepted = requested && !IsBitstreamActive(MusicUrl);
            IsEqualizerEnabled = accepted;

            ApplyEqToSession();
            return new EqStateResponse { IsEnabled = accepted, IsActive = accepted && _session is { Kind: RenderKind.Pcm } };
        }
    }

    private void ApplyEqToSession()
    {
        var session = _session;
        if (session == null || session.Kind != RenderKind.Pcm) return;
        session.Eq.Configure(session.SampleRate, IsEqualizerEnabled, EqGains);
    }

    // ─────────────── 设备枚举（IPC 面） ───────────────

    public (int id, string name)[] GetWasapiDevices()
    {
        var list = WasapiDeviceList.Enumerate();
        return list.Devices.Select(d => (d.Index, d.FriendlyName)).ToArray();
    }

    public (int id, string name)[] GetAsioDevices()
        => Win32.EnumerateAsioDrivers().Select((d, i) => (i, d.Name)).ToArray();

    // ─────────────── 结束看门狗 ───────────────

    private void WatchdogTick()
    {
        try
        {
            if (!IsPlaying) return;
            var session = _session;
            var output = _output;
            if (session == null) return;
            if (session.IsDrained)
            {
                lock (_streamLock)
                {
                    if (IsPlaying && session.IsDrained)
                    {
                        output?.Pause();
                        IsPlaying = false;
                        _ipc.PlayBackEnded(); // 自然结束：只发 PlayEnded（bass SyncFlags.End 对等）
                    }
                }
            }
        }
        catch { }
    }

    // ─────────────── 释放 ───────────────

    private void DisposeSession()
    {
        _output?.Dispose();
        _output = null;
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        _endedWatchdog?.Dispose();
        lock (_streamLock) DisposeSession();
    }
}
