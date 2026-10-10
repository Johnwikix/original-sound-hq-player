using System.Collections.Concurrent;
using BassPlayerIpc.Shared;

// AudioPlayer（BassPlayerSharp 替代）AOT 版冒烟测试：
// 充当主程序客户端：握手 IPC → SetMusicUrl → Play → 轮询进度 → EQ → 设备枚举 → MusicEnd。
// 用法: AudioPlayerSmokeTest <AOT AudioPlayer.exe 路径> <音频文件路径> [持续秒数]

if (args.Length < 2)
{
    Console.WriteLine("用法: AudioPlayerSmokeTest <AudioPlayer.exe> <音频文件> [秒]");
    return 2;
}
string exePath = Path.GetFullPath(args[0]);
string mediaPath = Path.GetFullPath(args[1]);
int runSeconds = args.Length > 2 && int.TryParse(args[2], out var rs) ? rs : 6;
string outputMode = args.FirstOrDefault(a => a.StartsWith("--mode="))?[7..] ?? "WasapiShared";
bool dop = args.Contains("--dop");
Console.WriteLine($"[smoke] 模式={outputMode} DoP={dop}");
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE")))
    Environment.SetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE", "smoke-" + Guid.NewGuid().ToString("N"));

// 1. 先持有客户端存活互斥体（服务端靠它确认主程序在场）
using var clientMutex = new Mutex(true, IpcConstants.ClientAliveMutexName);

// 2. 启动 AOT 服务端
using var server = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
{
    FileName = exePath,
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = Path.GetDirectoryName(exePath)!,
})!;
Console.WriteLine($"[smoke] server pid={server.Id}");

// 3. Connect both persistent pipes and verify they belong to the same player instance.
using var connectTimeout = new CancellationTokenSource(10000);
using var transport = PipeCommandClient.ConnectAsync(IpcConstants.ControlPipeName, connectTimeout.Token).GetAwaiter().GetResult();
using var states = PipeStateClient.ConnectAsync(IpcConstants.StatePipeName, connectTimeout.Token).GetAwaiter().GetResult();
if (transport.InstanceId != states.InstanceId) throw new InvalidOperationException("Player instance mismatch");
int playbackEnds = 0;
var notifications = new ConcurrentQueue<MessageTypeId>();
states.NotificationReceived += (type, _) =>
{
    if (type == MessageTypeId.PlayEnded) Interlocked.Increment(ref playbackEnds);
    notifications.Enqueue(type);
};
states.Start();
if (!SpinWait.SpinUntil(() => states.CurrentDspState is not null, 5000)) throw new InvalidOperationException("Initial DSP state missing");
Console.WriteLine("[smoke] Pipe 握手及初始 DSP 推送完成");
byte[] responseBuffer = new byte[IpcConstants.MaxResponseSize];

MessageTypeId Send(CommandId cmd, ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> respPayload)
{
    var (type, length) = transport.RequestAsync(cmd, payload, responseBuffer, timeoutMs: 5000).GetAwaiter().GetResult();
    respPayload = responseBuffer.AsSpan(0, Math.Max(0, length));
    return type;
}

void PumpNotifications()
{
    while (notifications.TryDequeue(out var type)) Console.WriteLine($"[smoke] 通知: {type}");
}

void StopPlayer()
{
    transport.Dispose();
    states.Dispose();
    if (!server.WaitForExit(10000))
    {
        server.Kill();
        server.WaitForExit();
        throw new InvalidOperationException("Player did not exit after pipe disconnect");
    }
}

Span<byte> urlBuf = new byte[BinarySerializer.SetMusicUrlRequestSize];
BinarySerializer.WriteSetMusicUrlRequest(urlBuf, new SetMusicUrlRequest { Url = mediaPath });
var t = Send(CommandId.SetMusicUrl, urlBuf, out _);
Console.WriteLine($"[smoke] SetMusicUrl → {t}");
PumpNotifications();

// 先发设置（共享 WASAPI、音量 0.15，避免冒烟测试太吵）
Span<byte> setBuf = new byte[1024];
int devIdx = -1;
var devArg = args.FirstOrDefault(a => a.StartsWith("--dev="));
if (devArg != null) int.TryParse(devArg[6..], out devIdx);
float vol = 1f;
var volArg = args.FirstOrDefault(a => a.StartsWith("--vol="));
if (volArg != null && float.TryParse(volArg[6..], out var v)) vol = v;
int setLen = BinarySerializer.WriteIpcSetting(setBuf, new IpcSetting
{
    OutputMode = outputMode,
    BassOutputDeviceId = devIdx,
    Latency = 300,
    IsDopEnabled = dop,
    DsdGain = 6,
    DsdPcmFreq = 88200,
    IsEqualizerEnabled = false,
    Volume = vol,
    IsSettingChanged = false,
    IsFadeEnabled = args.Contains("--fade"),
});
t = Send(CommandId.UpdateSettings, setBuf[..setLen], out _);
Console.WriteLine($"[smoke] UpdateSettings → {t}");

if (args.Contains("--devices-first"))
{
    Span<byte> devReq0 = new byte[1];
    BinarySerializer.WriteGetDevicesRequest(devReq0, new GetDevicesRequest { Page = 0 });
    var t0 = Send(CommandId.GetWasapiDevices, devReq0, out var devPage0);
    if (t0 == MessageTypeId.WasapiDevices)
    {
        var (page0, totalPages0, count0) = BinarySerializer.ReadDeviceListPageHeader(devPage0);
        Console.WriteLine($"[smoke] 播放前 WasapiDevices → 页{page0}/{totalPages0} 共{count0}台");
    }
    else Console.WriteLine($"[smoke] 播放前 WasapiDevices → {t0}");
}

Span<byte> playBuf = new byte[BinarySerializer.PlayRequestSize];
BinarySerializer.WritePlayRequest(playBuf, new PlayRequest { Url = mediaPath });
t = Send(CommandId.Play, playBuf, out _);
Console.WriteLine($"[smoke] Play(confirmed) → {t}（已收到执行确认）");
Thread.Sleep(400); // 观察首段输出与通知
PumpNotifications();

if (args.Contains("--fft-lifecycle"))
{
    try
    {
        int frames = 0;
        states.FftDataChanged += _ => Interlocked.Increment(ref frames);
        void SampleFftProcess(string phase)
        {
            server.Refresh();
            Console.WriteLine($"[fft-memory] {phase}: private={server.PrivateMemorySize64}, working={server.WorkingSet64}, handles={server.HandleCount}, threads={server.Threads.Count}, frames={Volatile.Read(ref frames)}");
        }
        if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing, 5000))
            throw new InvalidOperationException("PCM playback not ready");
        Thread.Sleep(2000);
        SampleFftProcess("baseline");
        for (int cycle = 0; cycle < 3; cycle++)
        {
            byte[] enabled = [1];
            if (Send(CommandId.SetFftEnabled, enabled, out _) != MessageTypeId.Success)
                throw new InvalidOperationException("FFT enable rejected");
            if (!SpinWait.SpinUntil(() => states.CurrentFftSnapshot is { IsAvailable: true }, 5000))
                throw new InvalidOperationException("FFT data missing");
            Thread.Sleep(2000);
            SampleFftProcess($"enabled-{cycle}");
            enabled[0] = 0;
            if (Send(CommandId.SetFftEnabled, enabled, out _) != MessageTypeId.Success)
                throw new InvalidOperationException("FFT disable rejected");
            if (!SpinWait.SpinUntil(() => states.CurrentFftSnapshot is { IsAvailable: false }, 5000))
                throw new InvalidOperationException("FFT disabled marker missing");
            int stoppedFrames = Volatile.Read(ref frames);
            Thread.Sleep(2000);
            if (Volatile.Read(ref frames) != stoppedFrames)
                throw new InvalidOperationException("FFT continues transmitting after disable");
            SampleFftProcess($"disabled-{cycle}");
        }
        Console.WriteLine("[smoke] PASS: FFT enable/disable/re-enable over real pipes, no frames after disable");
        return 0;
    }
    finally { StopPlayer(); }
}

// Memory experiment: one feature at a time; process counters are sampled by the client.
if (args.Contains("--memory"))
{
    try
    {
        string scenario = args.FirstOrDefault(a => a.StartsWith("--scenario="))?[11..] ?? "baseline";
        Console.WriteLine($"[memory-phase] {scenario}");
        var settings = new DspSettings
        {
            PlaybackRate = scenario == "rate5" ? 5 : scenario == "rate025" ? 0.25 : 1,
            CompressorEnabled = scenario == "compressor"
        };
        byte[] dspBytes = new byte[DspProtocol.SettingsSize];
        void UpdateMemoryDsp(DspSettings value)
        {
            DspProtocol.WriteSettings(dspBytes, value);
            if (Send(CommandId.UpdateDsp, dspBytes, out _) != MessageTypeId.Success)
                throw new InvalidOperationException("Memory scenario DSP rejected");
        }
        void SampleProcess(string phase)
        {
            server.Refresh();
            Console.WriteLine($"[memory-process] {phase},{server.PrivateMemorySize64},{server.WorkingSet64},{server.HandleCount},{server.Threads.Count}");
        }
        UpdateMemoryDsp(settings);
        if (scenario == "gapless")
        {
            if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing, 5000))
                throw new InvalidOperationException("Playback not ready");
            states.TryGetProgress(out var current);
            var request = new GaplessRequest(current.Epoch, 91, mediaPath);
            if (Send(CommandId.QueueNext, request.Write(), out _) != MessageTypeId.Success)
                throw new InvalidOperationException("Memory scenario preload rejected");
        }
        if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Playing && p.CurrentMs > 100, 5000))
            throw new InvalidOperationException("Memory scenario did not start rendering");
        Console.WriteLine("[memory-phase] measuring");
        if (scenario == "trackswitch")
        {
            int ReadPositiveOption(string option, int fallback)
            {
                string? value = args.FirstOrDefault(a => a.StartsWith(option, StringComparison.Ordinal))?[option.Length..];
                int result = value == null ? fallback : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (result <= 0) throw new ArgumentOutOfRangeException(option);
                return result;
            }
            int switches = ReadPositiveOption("--switch-count=", runSeconds);
            int interval = ReadPositiveOption("--switch-interval-ms=", 1000);
            string next = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--next="))?[7..] ?? mediaPath);
            byte[] switchPayload = new byte[BinarySerializer.PlayRequestSize];
            for (int index = 0; index < switches; index++)
            {
                states.TryGetProgress(out var before);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                BinarySerializer.WritePlayRequest(switchPayload, new PlayRequest { Url = index % 2 == 0 ? next : mediaPath });
                if (Send(CommandId.Play, switchPayload, out _) != MessageTypeId.Success)
                    throw new InvalidOperationException($"Track switch {index + 1} rejected");
                if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.Epoch != before.Epoch
                    && p.Playing && p.CurrentMs > 100, 5000))
                    throw new InvalidOperationException($"Track switch {index + 1} did not advance a new session");
                int remaining = interval - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
                if (remaining > 0) Thread.Sleep(remaining);
                if (!states.TryGetProgress(out var current) || !current.Playing || current.CurrentMs <= 100 || playbackEnds != 0)
                    throw new InvalidOperationException($"Track switch {index + 1} stopped prematurely");
                Console.WriteLine($"[memory-switch] {index + 1},{current.Epoch},{current.CurrentMs},{current.TotalMs}");
                SampleProcess("playing");
            }
        }
        else
        {
            long previousPosition = -1;
            for (int second = 0; second < runSeconds; second++)
            {
                if (scenario == "switch") UpdateMemoryDsp(settings with { PlaybackRate = second % 2 == 0 ? 1.5 : 1 });
                Thread.Sleep(1000);
                if (scenario == "baseline")
                {
                    if (!states.TryGetProgress(out var current) || !current.Playing || current.CurrentMs <= previousPosition || playbackEnds != 0)
                        throw new InvalidOperationException("Continuous playback stopped advancing");
                    previousPosition = current.CurrentMs;
                }
                SampleProcess("playing");
            }
        }
        Console.WriteLine("[smoke] PASS: memory scenario rendered throughout the measurement");
        Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty, out _);
        Console.WriteLine("[memory-phase] stopped");
        Thread.Sleep(2000);
        SampleProcess("stopped");
        if (!args.Contains("--no-collect"))
        {
            using var collect = EventWaitHandle.OpenExisting("Local\\AudioPlayerMemory-" + Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE"));
            collect.Set();
            Thread.Sleep(2500);
            SampleProcess("after-gc");
        }
        return 0;
    }
    finally { StopPlayer(); }
}

// Actual NativeAOT + persistent IPC + WASAPI integration for the new playback chain.
if (args.Contains("--gapless"))
{
    try
    {
        string? rateOption = args.FirstOrDefault(a => a.StartsWith("--rate="));
        double rate = rateOption == null ? 1.5 : double.Parse(rateOption[7..], System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(rate) || rate is < 0.25 or > 5) throw new ArgumentOutOfRangeException(nameof(rate));
        int drainTimeout = (int)Math.Ceiling(10000 / Math.Min(1, rate));
        Span<byte> dsp = stackalloc byte[DspProtocol.SettingsSize];
        DspProtocol.WriteSettings(dsp, new DspSettings { PlaybackRate = rate, CompressorEnabled = true });
        if (Send(CommandId.UpdateDsp, dsp, out _) != MessageTypeId.Success)
            throw new InvalidOperationException("DSP settings rejected");
        if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.PlaybackRate == rate && p.Playing, 5000))
            throw new InvalidOperationException("Speed change did not reach output telemetry");
        states.TryGetProgress(out var initial);
        long outputGeneration = states.CurrentDspState!.State.OutputGeneration;
        var queue = new GaplessRequest(initial.Epoch, 77, mediaPath);
        if (Send(CommandId.QueueNext, queue.Write(), out _) != MessageTypeId.Success)
            throw new InvalidOperationException("Gapless queue rejected");
        if (!SpinWait.SpinUntil(() => states.TryGetProgress(out var p) && p.GaplessToken == 77, drainTimeout))
            throw new InvalidOperationException("Gapless transition missing");
        states.TryGetProgress(out var transitioned);
        if (playbackEnds != 0 || !transitioned.Playing || transitioned.Epoch == initial.Epoch
            || transitioned.PlaybackRate != rate || states.CurrentDspState!.State.OutputGeneration != outputGeneration)
            throw new InvalidOperationException("Transition stopped or rebuilt the output");
        Console.WriteLine($"[smoke] PASS: gapless at {rate}x + compressor, unchanged native output, no PlayEnded at boundary");
        var pause = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var paused);
        if (pause != MessageTypeId.PlayState || BinarySerializer.ReadPlayStateResponse(paused).IsPlaying)
            throw new InvalidOperationException("Pause after transition failed");
        var resume = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var resumed);
        if (resume != MessageTypeId.PlayState || !BinarySerializer.ReadPlayStateResponse(resumed).IsPlaying)
            throw new InvalidOperationException("Resume after transition failed");
        if (!SpinWait.SpinUntil(() => playbackEnds > 0, drainTimeout) || playbackEnds != 1)
            throw new InvalidOperationException("Final output did not drain exactly once");
        Console.WriteLine("[smoke] PASS: pause/resume after adoption and exactly one final end notification");
        Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty, out _);
        return 0;
    }
    finally { StopPlayer(); }
}

// Natural end regression: real output + IPC, optionally seek near EOF to shorten a long fixture.
// --expect-end --fade --seek-near-end --next=<file> (omit --seek-near-end for a full playthrough).
if (args.Contains("--expect-end"))
{
    try
    {
        (long Current, long Total) Progress()
        {
            var type = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var payload);
            if (type != MessageTypeId.TimeProgress) throw new InvalidOperationException($"Progress response: {type}");
            return BinarySerializer.ReadTimeProgress(payload);
        }
        void Seek(long position)
        {
            Span<byte> payload = stackalloc byte[BinarySerializer.ChangePositionRequestSize];
            BinarySerializer.WriteChangePositionRequest(payload, new ChangePositionRequest { PositionMs = position });
            Send(CommandId.ChangePosition, payload, out _);
        }
        bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                PumpNotifications();
                if (condition()) return true;
                Thread.Sleep(25);
            } while (clock.ElapsedMilliseconds < timeoutMs);
            return false;
        }

        long duration = Progress().Total;
        if (args.Contains("--seek-near-end")) Seek(Math.Max(0, duration - 4000));
        if (!WaitUntil(() => playbackEnds > 0, checked(runSeconds * 1000)))
            throw new InvalidOperationException($"PlayEnded missing, position={Progress()}");
        if (playbackEnds != 1 || Math.Abs(Progress().Current - duration) > 200)
            throw new InvalidOperationException($"Duplicate or premature PlayEnded: count={playbackEnds}, position={Progress()}");
        Console.WriteLine($"[smoke] PASS: one PlayEnded after draining {duration}ms");

        Seek(0);
        var resumeType = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var resumeState);
        if (resumeType != MessageTypeId.PlayState || !BinarySerializer.ReadPlayStateResponse(resumeState).IsPlaying
            || !WaitUntil(() => Progress().Current > Math.Min(200, Math.Max(1, duration / 4)), 3000))
            throw new InvalidOperationException("Seek/resume after EOF did not advance");
        Console.WriteLine("[smoke] PASS: seek/resume after EOF advances");

        string? nextPath = args.FirstOrDefault(a => a.StartsWith("--next="))?[7..];
        if (nextPath != null)
        {
            BinarySerializer.WritePlayRequest(playBuf, new PlayRequest { Url = Path.GetFullPath(nextPath) });
            Send(CommandId.Play, playBuf, out _);
            if (!WaitUntil(() => { var p = Progress(); return p.Total != duration && p.Current > 200; }, 5000))
                throw new InvalidOperationException("Next fixture (with a different duration) did not start");
            Console.WriteLine("[smoke] PASS: next track starts with fading enabled");
        }
        Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty, out _);
        return 0;
    }
    finally
    {
        if (!server.HasExited) server.Kill();
        server.WaitForExit(3000);
    }
}

// 播放按钮确认状态（--no-toggle 时跳过，观察连续播放进度）
if (!args.Contains("--no-toggle"))
{
    t = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var ps);
    Console.WriteLine($"[smoke] PlayButton → {t}" + (ps.Length >= 1 ? $" payload={ps[0]}（1=播放中）" : " (载荷异常)"));
    PumpNotifications();
}

// EQ 全 +10dB（验证 EqState 双向；--no-eq 跳过——格式验证需要干净信号）
if (!args.Contains("--no-eq"))
{
    Span<byte> eqBuf = new byte[BinarySerializer.UpdateEqRequestSize];
    BinarySerializer.WriteUpdateEqRequest(eqBuf, new UpdateEqRequest
    {
        IsEnabled = true,
        Band0 = 10, Band1 = 8, Band2 = 6, Band3 = 4, Band4 = 2,
        Band5 = 0, Band6 = -2, Band7 = -4, Band8 = -6, Band9 = -8,
    });
    t = Send(CommandId.UpdateEq, eqBuf, out var eq);
    Console.WriteLine($"[smoke] UpdateEq → {t} len={eq.Length}" + (eq.Length >= 2 ? $" enabled={eq[0]} active={eq[1]}" : " (载荷异常)"));
}

// 设备枚举（分页第 0 页）
Span<byte> devReq = new byte[1];
BinarySerializer.WriteGetDevicesRequest(devReq, new GetDevicesRequest { Page = 0 });
t = Send(CommandId.GetWasapiDevices, devReq, out var devPage);
if (t == MessageTypeId.WasapiDevices)
{
    var (page, totalPages, count) = BinarySerializer.ReadDeviceListPageHeader(devPage);
    Console.WriteLine($"[smoke] WasapiDevices → 页{page}/{totalPages} 共{count}台");
    int off = BinarySerializer.DeviceListPageHeaderSize;
    for (int i = 0; i < count; i++)
    {
        var (id, name, read) = BinarySerializer.ReadDeviceEntry(devPage[off..]);
        off += read;
        Console.WriteLine($"[smoke]   [{id}] {name}");
    }
}

// 换曲测试：播放 1.5s 后对另一文件发 Play（触发换曲淡出：淡出→排空→切换→淡入）
if (args.Contains("--trackchange"))
{
    Thread.Sleep(1500);
    Span<byte> playBuf2 = new byte[BinarySerializer.PlayRequestSize];
    BinarySerializer.WritePlayRequest(playBuf2, new PlayRequest { Url = mediaPath });
    Send(CommandId.Play, playBuf2, out _);
    Console.WriteLine("[smoke] 换曲 Play 已发送");
    for (int i = 0; i < 8; i++) { Thread.Sleep(400); t = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var pr); if (t == MessageTypeId.TimeProgress) { var (c, tt) = BinarySerializer.ReadTimeProgress(pr); Console.WriteLine($"[smoke] 换曲后进度 {c / 1000.0:F1}s / {tt / 1000.0:F1}s"); } PumpNotifications(); }
    Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty, out _);
    StopPlayer();
    Console.WriteLine("[smoke] 换曲测试完成");
    return 0;
}

// 轮询进度
long lastCur = -1;
for (int i = 0; i < runSeconds * 4; i++)
{
    t = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var prog);
    if (t == MessageTypeId.TimeProgress)
    {
        var (curMs, totalMs) = BinarySerializer.ReadTimeProgress(prog);
        if (curMs != lastCur) Console.WriteLine($"[smoke] 进度 {curMs / 1000.0:F1}s / {totalMs / 1000.0:F1}s");
        lastCur = curMs;
    }
    PumpNotifications();
    Thread.Sleep(250);
}

if (!states.TryGetProgress(out var cachedProgress) || cachedProgress.TotalMs <= 0)
    throw new InvalidOperationException("Progress push cache did not receive a complete snapshot");
Console.WriteLine($"[smoke] 推送进度缓存: {cachedProgress.CurrentMs}/{cachedProgress.TotalMs}ms rev={cachedProgress.Revision}");
t = Send(CommandId.UpdateDeviceCorrections, DeviceCorrectionProtocol.Write(new()), out _);
if (t != MessageTypeId.Success) throw new InvalidOperationException("Device correction command failed");

// seek 测试
Span<byte> posBuf = new byte[BinarySerializer.ChangePositionRequestSize];
BinarySerializer.WriteChangePositionRequest(posBuf, new ChangePositionRequest { PositionMs = 5000 });
t = Send(CommandId.ChangePosition, posBuf, out _);
Console.WriteLine($"[smoke] ChangePosition(5s) → {t}");
Thread.Sleep(800);
t = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var prog2);
if (t == MessageTypeId.TimeProgress)
{
    var (curMs2, totalMs2) = BinarySerializer.ReadTimeProgress(prog2);
    Console.WriteLine($"[smoke] seek 后进度 {curMs2 / 1000.0:F1}s / {totalMs2 / 1000.0:F1}s");
}

// EOF 后 seek 仍处于停止状态；根据返回状态确认已暂停，再验证冻结与恢复。
bool TogglePlayback()
{
    var type = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var state);
    if (type != MessageTypeId.PlayState || state.Length < 1)
        throw new InvalidOperationException($"PlayButton 返回异常: {type}");
    return state[0] != 0;
}
long ReadPosition()
{
    var type = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var progress);
    if (type != MessageTypeId.TimeProgress)
        throw new InvalidOperationException($"进度返回异常: {type}");
    return BinarySerializer.ReadTimeProgress(progress).Item1;
}
if (TogglePlayback() && TogglePlayback())
    throw new InvalidOperationException("无法暂停播放");
long pausedPosition = ReadPosition();
Thread.Sleep(600);
long frozenPosition = ReadPosition();
if (frozenPosition != pausedPosition)
    throw new InvalidOperationException($"暂停进度未冻结: {pausedPosition} → {frozenPosition}");
Console.WriteLine($"[smoke] 暂停冻结验证通过: {frozenPosition}ms");
if (!TogglePlayback()) throw new InvalidOperationException("无法恢复播放");
Thread.Sleep(800);
long resumedPosition = ReadPosition();
if (resumedPosition <= frozenPosition)
    throw new InvalidOperationException($"恢复后进度未推进: {frozenPosition} → {resumedPosition}");
Console.WriteLine($"[smoke] 恢复推进验证通过: {frozenPosition} → {resumedPosition}ms");

// 收尾
t = Send(CommandId.MusicEnd, ReadOnlySpan<byte>.Empty, out _);
Console.WriteLine($"[smoke] MusicEnd → {t}");
Thread.Sleep(300);
t = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var prog4);
if (t == MessageTypeId.TimeProgress)
{
    var (curMs4, totalMs4) = BinarySerializer.ReadTimeProgress(prog4);
    Console.WriteLine($"[smoke] MusicEnd 后进度 {curMs4 / 1000.0:F1}s / {totalMs4 / 1000.0:F1}s（应回 0，总时长保留）");
}
PumpNotifications();

StopPlayer();
Console.WriteLine("[smoke] 完成");
return 0;
