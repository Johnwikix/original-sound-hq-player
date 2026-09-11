using AudioPlayer.Decode;
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
/// - DoP/NativeDSD 位流旁路全部 DSP，保存用户偏好并向 UI 提供实际会话状态；
/// - 自然结束只发 PlayEnded（不发 PlayState，与 bass SyncFlags.End 一致）；
/// - 进度由提交帧减去设备待播管线估算，精度受输出回调周期约束。
/// "DirectSound 全自动"语义（共享直传 + 端点通知）：
/// - 共享会话（DirectSound/WasapiShared）按源格式直传（AUTOCONVERTPCM），采样率/声道转换
///   交给音频引擎——系统改"输出音频格式"不再需要会话重建；
/// - EndpointNotifications 跟随默认设备切换/端点禁用拔出/格式变化，播放中静默换输出
///   （保解码环与进度），暂停中丢弃旧输出待恢复时落新设备；
/// - 输出失效（渲染失败）先静默快速恢复，穷尽后如实停机告知——UI 不再随暂态失效闪烁。
/// 淡入淡出（全 PCM 模式采样精确斜坡）竞态修复：
/// - 恢复播放递增停机令牌：取消在途的"淡出后延迟 Pause"，不再出现"UI 播放中、实际已停"；
/// - 换曲淡出单飞行任务 + 最新优先：淡出期间的新 PlayMusic 只更新 MusicUrl，到期切最新，
///   不再被重入保护丢弃；淡出期间被暂停则备好新会话不起播（PlayButton 直接续播新曲）；
/// - 斜坡 300ms（原 500ms）；WasapiShared 模式增益稳态回 1（音量由会话音量承担，不叠加）。
/// 独占换曲复用（ASIO 与 WASAPI 独占）：仅同设备、同采样率、同声道的 PCM 换源。
/// 采样率/声道/位流格式变化先停止并释放旧输出，再为新会话协商驱动格式和缓冲。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly PlayerIpcService _ipc;

    private const int FadeMs = 300; // 淡入/淡出斜坡时长

    private readonly object _streamLock = new();
    private Session? _session;
    private IAudioOutput? _output;
    private Timer? _endedWatchdog;
    private int _disposed;
    private int _fadeBusy; // Interlocked：换曲淡出任务单飞行（在途时新请求只更新 MusicUrl）
    private volatile bool _pauseFadeActive; // 暂停淡出进行中（音量同步让路）
    private int _pauseFadeToken; // 暂停淡出停机令牌：恢复播放/再次暂停递增，使在途延迟停机作废
    private long _playGen; // 用户操作代数（切歌/换设置/停止递增）：使在途失效恢复计划作废
    private long _outputStartedTick; // 当前输出生效时刻：用于识别"重建后秒挂"的设备抖动
    private int _fastFailStreak; // 连续快速失败（重建后 <4s 又挂）：达 2 次停止自动恢复
    private volatile string? _lastDefaultDeviceId; // 端点通知报告的系统默认设备 ID：独占复用门判断默认设备是否已变

    /// <summary>输出失效后的自动恢复计划。Silent=不改动播放状态与 UI（共享直传的暂态多数可静默恢复）。</summary>
    private sealed class RecoveryPlan
    {
        public long Gen; public long PositionMs; public int Attempts; public long NextTickMs; public bool Silent;
    }
    private RecoveryPlan? _recovery;

    public bool IsPlaying;
    public string OutputMode = "DirectSound";
    public int BassOutputDeviceId = -1;
    public string? WasapiEndpointId;
    public int BassASIODeviceId = 0;
    public int Latency = 300;
    public bool IsDopEnabled;
    public string? MusicUrl;
    public int DsdGain = 6;
    public int DsdPcmFreq = 88200;
    public bool IsEqualizerEnabled;
    public bool IsFadingEnabled;
    public float Volume = 0.5f;
    private DspSettings? _dspSettings = new();

    public readonly float[] EqGains = new float[10];
    public readonly float[] EqQ = Enumerable.Repeat(EqParameters.DefaultQ, 10).ToArray();

    private static readonly string DsfExtension = ".dsf";
    private static readonly string DffExtension = ".dff";

    public PlaybackEngine(PlayerIpcService ipc)
    {
        _ipc = ipc;
        _endedWatchdog = new Timer(_ => WatchdogTick(), null, 50, 50);
        // "DirectSound 全自动"的设备侧：默认设备/端点格式变化主动通知（注册失败退化为渲染失效探测）
        if (!EndpointNotifications.Start()) Console.WriteLine("[engine] endpoint notifications unavailable");
    }

    // ─────────────── 文件类别 ───────────────

    private static bool IsRawDsdContainer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(DsfExtension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(DffExtension, StringComparison.OrdinalIgnoreCase)
            || (ext.Equals(".wv", StringComparison.OrdinalIgnoreCase) && WavPackDsdReader.IsDsdFile(path));
    }

    /// <summary>当前曲目是否为位流路径（DoP / NativeDSD）。
    /// 模式判定与 IsSharedMode 同一精确集合：裸 "WasapiExclusive"（非法串）若被
    /// Contains 放行，会建出位流会话却落共享输出（无位流转换路径）→ 全程静音。</summary>
    private bool IsBitstreamActive(string? url) =>
        IsDopEnabled && !IsSharedMode(OutputMode) && IsRawDsdContainer(url);

    /// <summary>当前会话实际渲染种类（无会话时按设置预判）。</summary>
    private RenderKind EffectiveKind =>
        _session?.Kind ?? (IsBitstreamActive(MusicUrl)
            ? (OutputMode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop)
            : RenderKind.Pcm);

    /// <summary>软件增益的稳态目标：WasapiShared 的音量由会话音量承担（SetSessionVolume），
    /// 增益只承担淡入淡出形状，稳态必须回 1 否则音量被乘两次；其余模式增益即音量。</summary>
    private float GainVolumeTarget => OutputMode == "WasapiShared" ? 1f : Volume;

    // ─────────────── 播放控制（IPC 面） ───────────────

    public void PlayMusic(string musicUrl)
    {
        lock (_streamLock)
        {
            MusicUrl = musicUrl;
            if (IsFadingEnabled && IsPlaying && _session is { Kind: RenderKind.Pcm } && _output is { IsFailed: false })
            {
                // 换曲淡出：单飞行任务，期间的新请求只更新 MusicUrl（上面已赋值），到期切最新
                if (Interlocked.CompareExchange(ref _fadeBusy, 1, 0) == 0)
                    Task.Run(FadeOutAndSwitchAsync);
            }
            else
            {
                SwitchTo(musicUrl);
            }
        }
    }

    private void SwitchTo(string musicUrl)
    {
        Interlocked.Increment(ref _playGen);
        _recovery = null;
        var next = OpenSession(musicUrl); // 先开新会话：复用时旧会话须存活到源替换完成
        if (next != null && TryReuseExclusiveOutput(next))
        {
            var old = _session;
            _session = next;
            old?.Dispose(); // 输出已指向新源（回调整周期只认旧快照），旧会话安全释放
            AfterAttachToLiveOutput(resumeIfStopped: true);
            return;
        }
        DisposeSession();
        _session = next;
        if (_session != null) StartOutputAndPlay();
        else if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); }
    }

    /// <summary>备好新会话但不起播（换曲淡出期间被暂停）：UI 已显示新曲，PlayButton 直接续播新曲。</summary>
    private void PrepareNext(string url)
    {
        Interlocked.Increment(ref _playGen);
        _recovery = null;
        var next = OpenSession(url);
        if (next != null && TryReuseExclusiveOutput(next))
        {
            var old = _session;
            _session = next;
            old?.Dispose();
            next.RequestSeek(0);
            AfterAttachToLiveOutput(resumeIfStopped: false);
            return;
        }
        DisposeSession();
        _session = next;
        if (_session != null) _session.RequestSeek(0);
    }

    /// <summary>
    /// 独占输出仅复用同设备、同格式 PCM。格式变化返回 false，由调用方先 Dispose 旧输出
    /// 再创建新输出：ASIO 需重新协商缓冲/通道类型；WASAPI 需先退出渲染线程再释放 client，
    /// 不得在它仍使用旧格式/指针时原地重初始化。调用方持锁。
    /// </summary>
    private bool TryReuseExclusiveOutput(Session next)
    {
        if (IsSharedMode(OutputMode) || next.Kind != RenderKind.Pcm) return false;
        if (OutputMode == "ASIO" && _output is AsioOutput asio && !asio.IsFailed)
        {
            if (asio.SourceKind != RenderKind.Pcm) return false;
            if (asio.SourceChannels != next.Channels || asio.DeviceIndex != BassASIODeviceId) return false;
            return asio.AttachSource(next);
        }
        if (OutputMode is "WasapiExclusivePush" or "WasapiExclusiveEvent"
            && _output is WasapiOutput wasapi && wasapi.IsExclusive && !wasapi.IsFailed)
        {
            if (wasapi.SourceKind != RenderKind.Pcm) return false;
            if (wasapi.IsPushMode != (OutputMode == "WasapiExclusivePush")) return false;
            if (wasapi.DeviceIndex != BassOutputDeviceId) return false;
            if (!string.IsNullOrEmpty(WasapiEndpointId) && !string.Equals(wasapi.DeviceId, WasapiEndpointId, StringComparison.OrdinalIgnoreCase)) return false;
            // 跟随默认设备的输出：系统默认已变更则不复用（重建时会解析到新默认）
            if (wasapi.FollowsDefaultDevice && _lastDefaultDeviceId != null
                && !string.Equals(_lastDefaultDeviceId, wasapi.DeviceId ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
            return wasapi.AttachSource(next);
        }
        return false;
    }

    /// <summary>复用输出接上新会话后的收尾（StartOutputAndPlay 尾段对等）：
    /// 播放态/EQ/音量/淡入/抖动计时。<paramref name="resumeIfStopped"/>：SwitchTo 传入 true——
    /// 自然结束（PlayEnded 已停机）后切歌也要照常起播，与 StartOutputAndPlay 的
    /// "无会话起播即置播放态"语义对齐；PrepareNext（备播）传 false 保持停机。
    /// 驱动侧可能在排空/暂停时被 Stop：播放态下调用幂等 Resume，运行中的客户端不重复 Start。</summary>
    private void AfterAttachToLiveOutput(bool resumeIfStopped)
    {
        if (resumeIfStopped && !IsPlaying)
        {
            IsPlaying = true;
            _ipc.PlayStateUpdate(true);
        }
        if (IsPlaying) _output?.Resume();
        ApplyEqToSession();
        ApplyVolumeToOutput();
        _outputStartedTick = Environment.TickCount64;
    }

    /// <summary>输出失效后的重建（保进度）。调用方持有 _streamLock。恢复计划由调用方管理。</summary>
    private bool TryRebuildOutput(long positionMs)
    {
        var url = MusicUrl;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var kind = _session?.Kind;
        DisposeSession();
        _session = OpenSession(url, kindOverride: kind);
        if (_session == null) { if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); } return false; }
        if (positionMs > 0) _session.RequestSeek(positionMs);
        StartOutputAndPlay(); // 内部按成败发 PlayStateUpdate（仅在状态变化时）
        return IsPlaying && _output != null;
    }

    private Session? OpenSession(string url, bool forceSharedFormat = false, RenderKind? kindOverride = null)
    {
        // 回退共享时强制 PCM：位流会话（DoP/NativeDSD）在共享模式必然失败
        // 换曲复用会在旧会话仍存活时打开新会话。EffectiveKind 表示旧会话的实际格式
        //（可能已回退为 PCM/DoP），不能拿它决定新文件的解码/位流路径。
        var kind = forceSharedFormat ? RenderKind.Pcm : kindOverride
            ?? (IsBitstreamActive(url)
                ? (OutputMode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop)
                : RenderKind.Pcm);
        int? forcedRate = null;
        int? forcedChannels = null;
        int? maxChannels = null;
        if (kind == RenderKind.Pcm && (forceSharedFormat || IsSharedMode(OutputMode)))
        {
            if (forceSharedFormat)
            {
                // 独占/ASIO 失败回退共享的旧路径：会话精确按端点混音格式构建
                //（虚拟声卡常只接受混音格式，Senary 实测 44.1k 全拒）
                var mix = GetEndpointMixFormat(OutputMode == "DirectSound" || OutputMode == "ASIO" ? -1 : BassOutputDeviceId);
                if (mix != null)
                {
                    forcedRate = mix.SampleRate;
                    forcedChannels = mix.Channels;
                }
                else
                {
                    // 查询失败（设备重启/格式切换中）直接失败，交给恢复计划稍后重试
                    Console.WriteLine("[engine] mix format unavailable (device restarting?)");
                    return null;
                }
            }
            else
            {
                // 共享直传（AUTOCONVERTPCM）：源采样率直出，格式转换交给音频引擎；
                // 声道 >2 压到 2（EQ 只处理 ≤2 声道，下混在 swresample 完成）
                maxChannels = 2;
            }
        }
        var session = Session.Open(this, url, kind, DsdPcmFreq, DsdGain, Latency, forcedRate, forcedChannels, maxChannels);
        if (session != null)
        {
            session.ConfigureDsp(_dspSettings ?? new DspSettings());
            if (session.Gain != null)
            {
                session.Gain.SetImmediately(IsFadingEnabled ? 0 : GainVolumeTarget);
                if (IsFadingEnabled) session.Gain.RampTo(GainVolumeTarget, FadeMs);
            }
            if (session.Kind == RenderKind.Pcm)
                session.Eq.Configure(session.SampleRate, IsEqualizerEnabled, EqGains, EqQ);
        }
        if (session == null) Console.WriteLine($"[engine] OpenSession failed: kind={kind} url={url}");
        return session;
    }

    /// <summary>淡出后等待端点缓冲排空的余量（渲染领先可闻播放 LatencyMs）。</summary>
    private int FadeDrainMs => Math.Min(1500, (_output?.LatencyMs ?? 0) + 100);

    private static bool IsSharedMode(string mode) => mode is not ("WasapiExclusivePush" or "WasapiExclusiveEvent" or "ASIO");

    // 不缓存混音格式：系统改"输出音频格式"会变更混音格式而设备 ID 不变，陈旧缓存
    // 会让会话按旧率构建、端点按新率初始化 → 速率错配（变声）。每次现查（毫秒级）。
    private WasapiDeviceList.SharedMixFormat? GetEndpointMixFormat(int deviceIndex)
        => WasapiDeviceList.GetSharedMixFormat(deviceIndex, OutputMode is "DirectSound" or "ASIO" ? null : WasapiEndpointId).Mix;

    /// <summary>创建输出并开始播放；首选输出失败回退 WASAPI 共享（对应 bass 回退 DirectSound）。
    /// 播放状态只在变化时通知：静默恢复路径（设备切换/失效重建）不打扰 UI。</summary>
    private void StartOutputAndPlay()
    {
        var session = _session!;
        IAudioOutput? output = CreateOutput(session);
        if (output == null && OutputMode == "ASIO" && session.Kind == RenderKind.NativeDsd)
        {
            // ASIO native DSD 协商失败（驱动无 DSD 扩展/采样率域不符）→ 先试 ASIO DoP：
            // 仍走 ASIO 与位流，优于直接整体退到共享 PCM
            Console.WriteLine("[engine] asio native dsd failed, retry as dop");
            long keepMs = session.CurrentMs;
            DisposeSession();
            _session = OpenSession(MusicUrl!, kindOverride: RenderKind.Dop);
            if (_session != null)
            {
                _session.RequestSeek(keepMs);
                session = _session;
                output = CreateOutput(session);
            }
        }
        if (output == null && !IsSharedMode(OutputMode))
        {
            // 独占/ASIO 失败 → 回退共享：会话按混音格式重建（率/声道可能与源不同）
            Console.WriteLine("[engine] primary output failed, fallback to shared");
            long keepMs = session.CurrentMs; // 回退重建会话必须保留位置（否则从头播放）
            DisposeSession();
            _session = OpenSession(MusicUrl!, forceSharedFormat: true);
            if (_session == null) { if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); } return; }
            _session.RequestSeek(keepMs);
            output = CreateSharedOutput(_session);
            if (output == null) { if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); } return; }
        }
        if (output == null) { if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); } return; }
        _output = output;
        ApplyEqToSession();
        ApplyVolumeToOutput();
        _outputStartedTick = Environment.TickCount64;
        if (!IsPlaying)
        {
            IsPlaying = true;
            _ipc.PlayStateUpdate(true);
        }
    }

    private IAudioOutput? CreateOutput(Session session)
    {
        switch (OutputMode)
        {
            case "WasapiExclusivePush":
            case "WasapiExclusiveEvent":
            {
                var output = new WasapiOutput(true, OutputMode == "WasapiExclusivePush");
                if (output.Start(BassOutputDeviceId, Latency, session, 1, WasapiEndpointId)) return output;
                output.Dispose();
                return null;
            }
            case "ASIO":
            {
                var output = new AsioOutput();
                if (output.Start(BassASIODeviceId, session)) return output;
                output.Dispose();
                return null;
            }
            default: // DirectSound / WasapiShared → WASAPI 共享
                return CreateSharedOutput(session, OutputMode == "WasapiShared" ? BassOutputDeviceId : -1);
        }
    }


    private IAudioOutput? CreateSharedOutput(Session session, int deviceIndex = -1)
    {
        try
        {
            var output = new WasapiOutput(false, false);
            if (output.Start(deviceIndex, Latency, session, OutputMode == "WasapiShared" ? Volume : 1, OutputMode is "DirectSound" or "ASIO" ? null : WasapiEndpointId)) return output;
            if (!output.NeedsMixFormatSession)
            {
                output.Dispose();
                return null;
            }
            // 直传被拒（罕见）：退回旧路径——会话按端点混音格式重建后重试
            Console.WriteLine("[engine] autoconvert rejected, rebuild session at mix format");
            long keepMs = session.CurrentMs;
            var url = MusicUrl;
            output.Dispose();
            if (string.IsNullOrWhiteSpace(url)) return null;
            DisposeSession();
            _session = OpenSession(url, forceSharedFormat: true);
            if (_session == null) return null;
            _session.RequestSeek(keepMs);
            var retry = new WasapiOutput(false, false);
            if (retry.Start(deviceIndex, Latency, _session, OutputMode == "WasapiShared" ? Volume : 1,
                OutputMode is "DirectSound" or "ASIO" ? null : WasapiEndpointId)) return retry;
            retry.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[engine] shared fallback error: {ex.Message}");
            return null;
        }
    }

    /// <summary>只重建共享输出，会话/解码环/进度原地保留（默认设备切换、端点失效的静默恢复）。
    /// 调用方持锁；失败时输出已被释放，由调用方走停机/重试路径。</summary>
    private bool SwapSharedOutput()
    {
        try { _output?.Dispose(); } catch { }
        _output = null;
        var session = _session;
        if (session == null) return false;
        var output = CreateSharedOutput(session);
        if (output == null) return false;
        _output = output;
        ApplyEqToSession();
        ApplyVolumeToOutput();
        return true;
    }

    /// <summary>
    /// 静默恢复输出（不改动播放状态与 UI）。共享 PCM：会话与端点格式已解耦（AUTOCONVERTPCM），
    /// 只换输出原地续播；独占/ASIO：会话格式与设备绑定，保进度整会话重建。调用方持锁。
    /// </summary>
    private bool TryRecoverSilently(long positionMs)
    {
        var session = _session;
        if (session == null) return false;
        if (session.Kind == RenderKind.Pcm && IsSharedMode(OutputMode)) return SwapSharedOutput();
        var url = MusicUrl;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var kind = session.Kind;
        DisposeSession();
        _session = OpenSession(url, kindOverride: kind);
        if (_session == null) return false;
        if (positionMs > 0) _session.RequestSeek(positionMs);
        StartOutputAndPlay();
        return IsPlaying && _output != null && !_output.IsFailed;
    }

    public void PlayButton()
    {
        lock (_streamLock)
        {
            if (IsPlaying) PauseCore();
            else ResumeCore();
        }
    }

    /// <summary>暂停（持锁）：淡入淡出开启且输出健康时后台淡完再停机（延迟停机可被恢复取消）。</summary>
    private void PauseCore()
    {
        if (!IsPlaying) return;
        _recovery = null; // 用户暂停：接管，自动恢复计划作废
        if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null } && _output is { IsFailed: false })
        {
            // 淡出（bass：按下即通知，音频在后台淡完）。渲染领先可闻播放一个端点缓冲，
            // 必须等斜坡"排空到喇叭"再 Stop，否则尾段被硬裁
            _session.Gain.RampTo(0f, FadeMs);
            IsPlaying = false;
            _ipc.PlayStateUpdate(false);
            int token = ++_pauseFadeToken;
            _pauseFadeActive = true;
            int drain = FadeDrainMs;
            Task.Run(async () =>
            {
                await Task.Delay(FadeMs + drain);
                lock (_streamLock)
                {
                    // 令牌已递增（期间恢复了播放/再次暂停）或已回播放态 = 本次停机被取消
                    if (token == _pauseFadeToken && !IsPlaying && _session != null)
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
            _ipc.PlayStateUpdate(false);
        }
    }

    /// <summary>恢复/起播（持锁）。</summary>
    private void ResumeCore()
    {
        // 关键（旧实现错位根源）：淡出已调度"延迟停机"，恢复若不递增令牌，
        // 延迟任务到点仍会 Pause 掉刚恢复的输出 → UI 播放中、实际静音
        _pauseFadeToken++;
        _pauseFadeActive = false;
        if (_session == null)
        {
            if (!string.IsNullOrWhiteSpace(MusicUrl))
            {
                SwitchTo(MusicUrl);
                return;
            }
        }
        else if (_output is { IsFailed: true })
        {
            // 输出已死（设备格式切换/失效）：Resume 无意义，保进度重建
            Interlocked.Increment(ref _playGen);
            TryRebuildOutput(_session.CurrentMs);
            return;
        }
        else if (_output == null)
        {
            // 会话已备好（换曲淡出期间被暂停 / 端点切换时丢弃了旧输出）：直接起播
            StartOutputAndPlay();
            return;
        }
        else
        {
            _output.Resume();
            if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
                _session.Gain.RampTo(GainVolumeTarget, FadeMs);
        }
        IsPlaying = true;
        _ipc.PlayStateUpdate(true);
    }

    /// <summary>
    /// 换曲淡出（bass 规则：剩余&lt;3s 或时长未知跳过；时长 min(剩余/2,300)ms）。单飞行任务：
    /// ① 淡出期间的新 PlayMusic 只更新 MusicUrl（最新优先），本任务到期切到最新——旧实现
    /// 会被重入保护直接丢弃，造成"UI 显示新曲、实际继续播旧曲"；② 淡出期间被暂停时备好
    /// 新会话不起播，PlayButton 直接续播新曲。
    /// </summary>
    private async Task FadeOutAndSwitchAsync()
    {
        try
        {
            int fadeMs;
            int drain;
            lock (_streamLock)
            {
                if (_session is not { Kind: RenderKind.Pcm, Gain: not null } || _output is not { IsFailed: false })
                {
                    Interlocked.Exchange(ref _fadeBusy, 0);
                    return; // 会话已被其他操作换掉/释放：MusicUrl 保留，后续 Play 会起播
                }
                var (curMs, totalMs) = GetTimeProgress();
                long remainingMs = totalMs - curMs;
                if (remainingMs < 3000 || totalMs <= 0)
                {
                    SwitchTo(MusicUrl ?? string.Empty);
                    Interlocked.Exchange(ref _fadeBusy, 0);
                    return;
                }
                fadeMs = (int)Math.Min(remainingMs / 2, FadeMs);
                _session.Gain.RampTo(0f, fadeMs);
                drain = FadeDrainMs;
            }
            await Task.Delay(fadeMs + drain).ConfigureAwait(false);
            lock (_streamLock)
            {
                try
                {
                    // MusicUrl 现值 = 最新请求（期间的新 PlayMusic 只更新了它）
                    if (_session is { Kind: RenderKind.Pcm })
                    {
                        if (IsPlaying) SwitchTo(MusicUrl ?? string.Empty);
                        else PrepareNext(MusicUrl ?? string.Empty);
                    }
                }
                finally
                {
                    // 与 PlayMusic 的 CAS 同锁释放：不存在"忙但已切完"的丢失窗口
                    Interlocked.Exchange(ref _fadeBusy, 0);
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _fadeBusy, 0);
        }
    }

    public void FadeOut()
    {
        lock (_streamLock)
        {
            PauseCore();
        }
    }

    public void MusicEnd()
    {
        lock (_streamLock)
        {
            Interlocked.Increment(ref _playGen);
            _recovery = null; // 用户停止：取消自动恢复
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
            var plan = _recovery; // 失效暂停期间手动 seek：恢复时落到新位置
            if (plan != null) plan.PositionMs = Math.Max(0, positionMs);
        }
    }

    public void SetVolume(double volume)
    {
        lock (_streamLock)
        {
            Volume = double.IsFinite(volume) ? (float)Math.Clamp(volume, 0, 1) : 0;
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
        long anchor = session.FramesToMs(Volatile.Read(ref session.AnchorFrames));
        long cur = Math.Min(Math.Max(anchor, session.CurrentMs - (_output?.PendingAudioMs ?? 0)),
            total > 0 ? total : long.MaxValue);
        return (cur, total);
    }

    public void UpdateSettings(IpcSetting s)
    {
        lock (_streamLock)
        {
            if (s.OutputMode is not (null or "DirectSound" or "WasapiShared" or "WasapiExclusivePush" or "WasapiExclusiveEvent" or "ASIO"))
                throw new ArgumentException("Unknown audio output mode", nameof(s));
            bool rebuild = OutputMode != (s.OutputMode ?? "DirectSound") || BassOutputDeviceId != s.BassOutputDeviceId
                || WasapiEndpointId != s.WasapiEndpointId || BassASIODeviceId != s.BassASIODeviceId
                || Latency != Math.Clamp(s.Latency, 10, 2000) || IsDopEnabled != s.IsDopEnabled
                || DsdGain != Math.Clamp(s.DsdGain, -24, 24) || DsdPcmFreq != Math.Clamp(s.DsdPcmFreq, 8000, 768000);
            OutputMode = s.OutputMode ?? "DirectSound";
            BassOutputDeviceId = s.BassOutputDeviceId;
            WasapiEndpointId = s.WasapiEndpointId;
            BassASIODeviceId = s.BassASIODeviceId;
            Latency = Math.Clamp(s.Latency, 10, 2000);
            IsDopEnabled = s.IsDopEnabled;
            DsdGain = Math.Clamp(s.DsdGain, -24, 24);
            DsdPcmFreq = Math.Clamp(s.DsdPcmFreq, 8000, 768000);
            Volume = float.IsFinite(s.Volume) ? Math.Clamp(s.Volume, 0, 1) : 0;
            IsFadingEnabled = s.IsFadeEnabled;
            IsEqualizerEnabled = s.IsEqualizerEnabled;
            ApplyEqToSession();
            if (s.IsSettingChanged || (rebuild && _session != null)) ChangingSetting();
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
                Interlocked.Increment(ref _playGen);
                _recovery = null; // 用户改设置：接管恢复
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
                else
                {
                    StopAndNotifyLocked();
                    throw new InvalidOperationException("Audio session could not apply output settings");
                }
                if (wasPlaying && _output == null)
                    throw new InvalidOperationException("Audio output could not restart after settings change");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[engine] settings change failed: {ex.Message}");
            throw;
        }
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
            EqQ[0] = (float)EqParameters.NormalizeQ(req.Q0);
            EqQ[1] = (float)EqParameters.NormalizeQ(req.Q1);
            EqQ[2] = (float)EqParameters.NormalizeQ(req.Q2);
            EqQ[3] = (float)EqParameters.NormalizeQ(req.Q3);
            EqQ[4] = (float)EqParameters.NormalizeQ(req.Q4);
            EqQ[5] = (float)EqParameters.NormalizeQ(req.Q5);
            EqQ[6] = (float)EqParameters.NormalizeQ(req.Q6);
            EqQ[7] = (float)EqParameters.NormalizeQ(req.Q7);
            EqQ[8] = (float)EqParameters.NormalizeQ(req.Q8);
            EqQ[9] = (float)EqParameters.NormalizeQ(req.Q9);


            bool requested = req.IsEnabled;
            // 位流模式（DoP/NativeDSD）不应用 EQ，响应仍报告实际是否可用。
            bool accepted = requested && (_dspSettings?.IsEnabled ?? true) && EffectiveKind == RenderKind.Pcm;
            // 保存用户偏好；实际旁路按当前会话判定，回退 PCM 时也能正确恢复。
            IsEqualizerEnabled = requested;

            ApplyEqToSession();
            return new EqStateResponse { IsEnabled = accepted, IsActive = accepted && _session is { Kind: RenderKind.Pcm } };
        }
    }

    private void ApplyEqToSession()
    {
        var session = _session;
        if (session == null || session.Kind != RenderKind.Pcm) return;
        session.Eq.Configure(session.SampleRate, IsEqualizerEnabled, EqGains, EqQ);
    }

    /// <summary>更新 PCM 音效；不重建输出，不接触 DoP/Native DSD 位流。</summary>
    public void UpdateDsp(DspSettings settings)
    {
        lock (_streamLock)
        {
            var normalized = settings.Sanitize();
            if (_dspSettings == normalized) return;
            _dspSettings = normalized;
            _session?.ConfigureDsp(_dspSettings);
        }
    }

    /// <summary>读取实际会话状态，供已打开的音效界面持续同步。</summary>
    public DspState GetDspState()
    {
        lock (_streamLock)
        {
            byte kind = (byte)(_session?.Kind ?? RenderKind.Pcm);
            bool enabled = _dspSettings?.IsEnabled ?? true;
            bool eq = enabled && kind == 0 && IsEqualizerEnabled && (_session == null || _session.Channels <= 2);
            return _session?.Effects?.GetState(kind, eq)
                ?? new DspState(kind, eq, _session?.Channels ?? 0, LoudnessStatus.Off, 0, double.NaN, enabled);
        }
    }

    // ─────────────── 设备枚举（IPC 面） ───────────────

    public (int id, string name, string endpoint)[] GetWasapiDevices()
    {
        var list = WasapiDeviceList.Enumerate();
        return list.Devices.Select(d => (d.Index, d.FriendlyName, d.Id)).ToArray();
    }

    public (int id, string name)[] GetAsioDevices()
        => Win32.EnumerateAsioDrivers().Select((d, i) => (i, d.Name)).ToArray();

    // ─────────────── 结束看门狗 ───────────────

    private void WatchdogTick()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var plan = _recovery;
            if (plan != null) { TickRecovery(plan); return; }
            HandleEndpointEvents();
            if (!IsPlaying) return;
            var session = _session;
            var output = _output;
            if (session == null) return;
            if (output is { IsFailed: true })
            {
                if (output is AsioOutput { IsRestartPending: true, IsRestartReady: false }) return;
                lock (_streamLock)
                {
                    if (IsPlaying && ReferenceEquals(output, _output) && output.IsFailed)
                    {
                        // 重建后 4 秒内又挂 = 设备抖动：连续两次后交还用户，避免播放/暂停每秒抖动
                        bool driverReconfigured = output is AsioOutput { IsRestartPending: true };
                        bool fastFail = !driverReconfigured && Environment.TickCount64 - _outputStartedTick < 4000;
                        _fastFailStreak = fastFail ? _fastFailStreak + 1 : 0;
                        if (_fastFailStreak >= 2)
                        {
                            Console.WriteLine("[engine] output keeps failing fast, auto-recovery suspended");
                            StopAndNotifyLocked();
                            return;
                        }
                        long pos = session.CurrentMs;
                        Console.WriteLine(driverReconfigured
                            ? "[engine] ASIO driver configuration changed, rebuilding output"
                            : "[engine] output failed or callbacks stalled, silent recovery");
                        if (driverReconfigured)
                        {
                            // 主动改驱动设置不算设备故障。先释放旧输出，给驱动短暂稳定时间；
                            // 解码会话/进度保留，用户暂停或换曲会取消这份计划。
                            output.Dispose();
                            _output = null;
                            _recovery = new RecoveryPlan
                            {
                                Gen = Volatile.Read(ref _playGen), PositionMs = pos,
                                NextTickMs = Environment.TickCount64 + 250, Silent = true,
                            };
                            return;
                        }
                        // 先静默快速恢复（不改动播放状态，UI 不闪烁）；失败再计划延迟静默重试，
                        // 重试穷尽才如实停机——格式切换/设备重启等暂态对用户完全透明
                        if (TryRecoverSilently(pos))
                        {
                            _outputStartedTick = Environment.TickCount64;
                            return;
                        }
                        _recovery = new RecoveryPlan
                        {
                            Gen = Volatile.Read(ref _playGen),
                            PositionMs = pos,
                            Attempts = 0,
                            NextTickMs = Environment.TickCount64 + 250,
                            Silent = true,
                        };
                    }
                }
                return;
            }
            if (session.IsDrained)
            {
                lock (_streamLock)
                {
                    if (Volatile.Read(ref _disposed) == 0 && IsPlaying && ReferenceEquals(session, _session)
                        && ReferenceEquals(output, _output) && session.IsDrained && output is { IsDrained: true })
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

    /// <summary>
    /// 端点通知消费（共享模式的"全自动"设备侧，ECHO DeviceWatcher 对等）：
    /// 默认设备切换 → 跟随换输出；当前端点被禁用/拔出/系统改输出格式 → 换输出。
    /// 仅共享 PCM 且事件与当前输出相关时行动；位流/独占/ASIO 的会话与设备绑定，
    /// 仍走失效→重建路径。播放中静默换输出（保解码环与进度，UI 无感）；
    /// 暂停中只丢弃旧输出，PlayButton 恢复时按新设备起播。
    /// </summary>
    private void HandleEndpointEvents()
    {
        if (!EndpointNotifications.TryDrain(out var defaultId, out var events)) return;
        if (defaultId != null) _lastDefaultDeviceId = defaultId;
        lock (_streamLock)
        {
            if (_output is not WasapiOutput wasapi || _session == null) return;
            bool relevant = false;
            if (wasapi.FollowsDefaultDevice && defaultId != null
                && !string.Equals(defaultId, wasapi.DeviceId ?? "", StringComparison.OrdinalIgnoreCase))
                relevant = true;
            foreach (var (kind, deviceId) in events)
            {
                if (!string.Equals(deviceId, wasapi.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (kind != EndpointEventKind.StateChanged) relevant = true; // Removed / FormatChanged
            }
            if (!relevant) return;
            if (_session.Kind != RenderKind.Pcm || !IsSharedMode(OutputMode)) return;
            if (IsPlaying)
            {
                Console.WriteLine("[engine] endpoint changed, silent output swap");
                if (SwapSharedOutput())
                {
                    _outputStartedTick = Environment.TickCount64;
                    _fastFailStreak = 0;
                    return;
                }
                _recovery = new RecoveryPlan
                {
                    Gen = Volatile.Read(ref _playGen),
                    PositionMs = _session.CurrentMs,
                    Attempts = 0,
                    NextTickMs = Environment.TickCount64 + 250,
                    Silent = true,
                };
            }
            else
            {
                _output?.Dispose();
                _output = null; // 暂停中：PlayButton 恢复时按当前默认设备重新起播
            }
        }
    }

    /// <summary>停机并如实上报（持锁）：静默恢复穷尽/快速失败抖动后的最终让位。</summary>
    private void StopAndNotifyLocked()
    {
        try { _output?.Pause(); } catch { }
        if (IsPlaying)
        {
            IsPlaying = false;
            _ipc.PlayStateUpdate(false);
        }
    }

    /// <summary>
    /// 自动恢复节拍。Silent：+250ms/+500ms/+1s/+2s 四次静默重试，穷尽后如实停机告知；
    /// 非静默：+1s/+3s/+7s 三次重建（保进度）。用户操作（代数变化）即作废。
    /// </summary>
    private void TickRecovery(RecoveryPlan plan)
    {
        if (Volatile.Read(ref _playGen) != plan.Gen) { _recovery = null; return; }
        if (Environment.TickCount64 < plan.NextTickMs) return;
        lock (_streamLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(plan, _recovery)) return;
            if (Volatile.Read(ref _playGen) != plan.Gen) { _recovery = null; return; }
            plan.Attempts++;
            Console.WriteLine($"[engine] output recovery attempt {plan.Attempts}{(plan.Silent ? " (silent)" : "")}");
            bool recovered = plan.Silent ? TryRecoverSilently(plan.PositionMs) : TryRebuildOutput(plan.PositionMs);
            if (recovered)
            {
                _recovery = null; // 必须清除：否则下次 tick 计划时间已过 → 无限销毁刚建的会话再重建
                _outputStartedTick = Environment.TickCount64;
                _fastFailStreak = 0;
                return;
            }
            if (plan.Silent)
            {
                if (plan.Attempts >= 4)
                {
                    Console.WriteLine("[engine] silent recovery exhausted, stop");
                    _recovery = null;
                    StopAndNotifyLocked();
                    return;
                }
                plan.NextTickMs = Environment.TickCount64 + (plan.Attempts switch { 1 => 500, 2 => 1000, _ => 2000 });
                return;
            }
            if (plan.Attempts >= 3) { _recovery = null; return; } // 放弃：等用户手动操作
            plan.NextTickMs = Environment.TickCount64 + (plan.Attempts == 1 ? 2000 : 4000);
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_endedWatchdog != null)
        {
            using var stopped = new ManualResetEvent(false);
            if (_endedWatchdog.Dispose(stopped)) stopped.WaitOne();
        }
        EndpointNotifications.Stop();
        lock (_streamLock)
        {
            Interlocked.Increment(ref _playGen);
            _recovery = null;
            IsPlaying = false;
            DisposeSession();
        }
    }
}
