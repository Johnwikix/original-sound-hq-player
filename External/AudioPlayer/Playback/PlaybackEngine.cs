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
/// 独占换曲复用（ASIO 与 WASAPI 独占，PCM↔PCM 同设备）：保活输出只换渲染源——同格式零设备交互
/// （无缝交接）；ASIO 换率 Stop→SetSampleRate→Start（缓冲按样本数计不重建）、WASAPI 独占换格式
/// 保设备指针/渲染线程重激活重协商；位流/设备变化仍全量重建。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly PlayerIpcService _ipc;

    private const int FadeMs = 300; // 淡入/淡出斜坡时长

    private readonly object _streamLock = new();
    private Session? _session;
    private IAudioOutput? _output;
    private Timer? _endedWatchdog;
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
        // "DirectSound 全自动"的设备侧：默认设备/端点格式变化主动通知（注册失败退化为渲染失效探测）
        if (!EndpointNotifications.Start()) Console.WriteLine("[engine] endpoint notifications unavailable");
    }

    // ─────────────── 文件类别 ───────────────

    private static bool IsRawDsdContainer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(DsfExtension, StringComparison.OrdinalIgnoreCase)
            || ext.Equals(DffExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前曲目是否为位流路径（DoP / NativeDSD）。
    /// 模式判定与 IsSharedMode 同一精确集合：裸 "WasapiExclusive"（非法串）若被
    /// Contains 放行，会建出位流会话却落共享输出（无位流转换路径）→ 全程静音。</summary>
    private bool IsBitstreamActive(string? url) =>
        IsDopEnabled && IsRawDsdContainer(url) && !IsSharedMode(OutputMode);

    /// <summary>当前会话实际渲染种类（无会话时按设置预判）。</summary>
    private RenderKind EffectiveKind =>
        _session?.Kind ?? (IsBitstreamActive(MusicUrl)
            ? (OutputMode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop)
            : RenderKind.Pcm);

    /// <summary>软件增益的稳态目标：WasapiShared 的音量由会话音量承担（SetSessionVolume），
    /// 增益只承担淡入淡出形状，稳态必须回 1 否则音量被乘两次；其余模式增益即音量。</summary>
    private float GainVolumeTarget => OutputMode == "WasapiShared" ? 1f : Volume;

    // ─────────────── 播放控制（IPC 面） ───────────────

    public void PlayMusic(string musicUrl, bool isSettingChanged = false)
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
    /// 独占输出换曲复用（PCM↔PCM、同设备）：保活输出只换渲染源。
    /// ASIO：同率纯引用替换（零驱动交互），换率 Stop→SetSampleRate→Start（缓冲按样本数计不重建）；
    /// WASAPI 独占：同格式纯引用替换，率/声道变化走 ReinitializeFor（保设备指针/事件/渲染线程，
    /// 仅重激活 client 重新协商）。位流↔PCM、设备变更、输出失效仍全量重建。调用方持锁。
    /// </summary>
    private bool TryReuseExclusiveOutput(Session next)
    {
        if (IsSharedMode(OutputMode) || next.Kind != RenderKind.Pcm) return false;
        if (_output is AsioOutput asio && !asio.IsFailed)
        {
            if (asio.SourceKind != RenderKind.Pcm) return false;
            if (asio.SourceChannels != next.Channels || asio.DeviceIndex != BassASIODeviceId) return false;
            return asio.SourceSampleRate == next.SampleRate
                ? asio.AttachSource(next)
                : asio.ChangeSourceAndRate(next);
        }
        if (_output is WasapiOutput wasapi && wasapi.IsExclusive && !wasapi.IsFailed)
        {
            if (wasapi.SourceKind != RenderKind.Pcm) return false;
            if (wasapi.DeviceIndex != BassOutputDeviceId) return false;
            // 跟随默认设备的输出：系统默认已变更则不复用（重建时会解析到新默认）
            if (wasapi.FollowsDefaultDevice && _lastDefaultDeviceId != null
                && !string.Equals(_lastDefaultDeviceId, wasapi.DeviceId ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
            return wasapi.SourceSampleRate == next.SampleRate && wasapi.SourceChannels == next.Channels
                ? wasapi.AttachSource(next)
                : wasapi.ReinitializeFor(next);
        }
        return false;
    }

    /// <summary>复用输出接上新会话后的收尾（StartOutputAndPlay 尾段对等）：
    /// 播放态/EQ/音量/淡入/抖动计时。<paramref name="resumeIfStopped"/>：SwitchTo 传入 true——
    /// 自然结束（PlayEnded 已停机）后切歌也要照常起播，与 StartOutputAndPlay 的
    /// "无会话起播即置播放态"语义对齐；PrepareNext（备播）传 false 保持停机。
    /// 驱动侧可能在排空/暂停时被 Stop：播放态下补一次 Resume（运行中重复 Start 返回错误码，无害）。</summary>
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
        if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
        {
            // 与 StartOutputAndPlay 同序：ApplyVolumeToOutput 先 RampTo，随后归零铺斜坡。
            // 预缓冲静音段不推进斜坡，出声即从 0 起（见 Session.FillPcm）
            _session.Gain.SetImmediately(0f);
            if (IsPlaying) _session.Gain.RampTo(GainVolumeTarget, FadeMs);
            // 备播（换曲淡出期间被暂停）：归零静默待命，ResumeCore 的 RampTo 承担可闻淡入
        }
        _outputStartedTick = Environment.TickCount64;
    }

    /// <summary>输出失效后的重建（保进度）。调用方持有 _streamLock。恢复计划由调用方管理。</summary>
    private bool TryRebuildOutput(long positionMs)
    {
        var url = MusicUrl;
        if (string.IsNullOrWhiteSpace(url)) return false;
        DisposeSession();
        _session = OpenSession(url);
        if (_session == null) { if (IsPlaying) { IsPlaying = false; _ipc.PlayStateUpdate(false); } return false; }
        if (positionMs > 0) _session.RequestSeek(positionMs);
        StartOutputAndPlay(); // 内部按成败发 PlayStateUpdate（仅在状态变化时）
        return IsPlaying && _output != null;
    }

    private Session? OpenSession(string url, bool forceSharedFormat = false, RenderKind? kindOverride = null)
    {
        // 回退共享时强制 PCM：位流会话（DoP/NativeDSD）在共享模式必然失败
        var kind = forceSharedFormat ? RenderKind.Pcm : kindOverride ?? EffectiveKind;
        int? forcedRate = null;
        int? forcedChannels = null;
        int? maxChannels = null;
        if (kind == RenderKind.Pcm && (forceSharedFormat || IsSharedMode(OutputMode)))
        {
            if (forceSharedFormat)
            {
                // 独占/ASIO 失败回退共享的旧路径：会话精确按端点混音格式构建
                //（虚拟声卡常只接受混音格式，Senary 实测 44.1k 全拒）
                var mix = GetEndpointMixFormat(IsSharedDeviceIndexed(OutputMode) ? BassOutputDeviceId : -1);
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
        if (session == null) Console.WriteLine($"[engine] OpenSession failed: kind={kind} url={url}");
        return session;
    }

    /// <summary>淡出后等待端点缓冲排空的余量（渲染领先可闻播放 LatencyMs）。</summary>
    private int FadeDrainMs => Math.Min(1500, (_output?.LatencyMs ?? 0) + 100);

    private static bool IsSharedMode(string mode) => mode is not ("WasapiExclusivePush" or "WasapiExclusiveEvent" or "ASIO");
    private static bool IsSharedDeviceIndexed(string mode) => mode == "WasapiShared";

    // 不缓存混音格式：系统改"输出音频格式"会变更混音格式而设备 ID 不变，陈旧缓存
    // 会让会话按旧率构建、端点按新率初始化 → 速率错配（变声）。每次现查（毫秒级）。
    private static WasapiDeviceList.SharedMixFormat? GetEndpointMixFormat(int deviceIndex)
        => WasapiDeviceList.GetSharedMixFormat(deviceIndex).Mix;

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
        if (IsFadingEnabled && _session is { Kind: RenderKind.Pcm, Gain: not null })
        {
            // 淡入（bass 语义：每次起播/换曲都生效）。必须在 ApplyVolumeToOutput 之后：
            // 音量同步会先 RampTo，随后归零并铺 300ms 斜坡。
            // 预缓冲静音段不推进斜坡（见 Session.FillPcm），出声即从 0 起
            _session.Gain.SetImmediately(0f);
            _session.Gain.RampTo(GainVolumeTarget, FadeMs);
        }
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

    // ASIO 请求缓冲固定 300ms，不随 Latency 设置（有意设计）
    private static int ExclusiveBufferFrames(Session session) => (int)((long)session.SampleRate * 300 / 8000);

    private IAudioOutput? CreateSharedOutput(Session session, int deviceIndex = -1)
    {
        try
        {
            var output = new WasapiOutput(false, false);
            if (output.Start(deviceIndex, Latency, session)) return output;
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
            return retry.Start(deviceIndex, Latency, _session) ? retry : null;
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
        DisposeSession();
        _session = OpenSession(url);
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
    private async void FadeOutAndSwitchAsync()
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
                else StopAndNotifyLocked();
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
            var plan = _recovery;
            if (plan != null) { TickRecovery(plan); return; }
            HandleEndpointEvents();
            if (!IsPlaying) return;
            var session = _session;
            var output = _output;
            if (session == null) return;
            if (output is { IsFailed: true })
            {
                lock (_streamLock)
                {
                    if (IsPlaying && ReferenceEquals(output, _output) && output.IsFailed)
                    {
                        // 重建后 4 秒内又挂 = 设备抖动：连续两次后交还用户，避免播放/暂停每秒抖动
                        bool fastFail = Environment.TickCount64 - _outputStartedTick < 4000;
                        _fastFailStreak = fastFail ? _fastFailStreak + 1 : 0;
                        if (_fastFailStreak >= 2)
                        {
                            Console.WriteLine("[engine] output keeps failing fast, auto-recovery suspended");
                            StopAndNotifyLocked();
                            return;
                        }
                        long pos = session.CurrentMs;
                        Console.WriteLine("[engine] output failed, silent recovery");
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
            if (!ReferenceEquals(plan, _recovery)) return;
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
                plan.NextTickMs = Environment.TickCount64 + plan.Attempts switch { 1 => 500, 2 => 1000, _ => 2000 };
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
        _endedWatchdog?.Dispose();
        EndpointNotifications.Stop();
        lock (_streamLock) DisposeSession();
    }
}
