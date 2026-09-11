using System.IO.MemoryMappedFiles;
using BassPlayerIpc.Shared;

// AudioPlayer（BassPlayerSharp 替代）AOT 版冒烟测试：
// 充当主程序客户端：握手 IPC → SetMusicUrl → Play → 轮询进度 → EQ → 设备枚举 → MusicEnd。
// 用法: AudioPlayerSmokeTest <AOT BassPlayerSharp.exe 路径> <音频文件路径> [持续秒数]

if (args.Length < 2)
{
    Console.WriteLine("用法: AudioPlayerSmokeTest <BassPlayerSharp.exe> <音频文件> [秒]");
    return 2;
}
string exePath = Path.GetFullPath(args[0]);
string mediaPath = Path.GetFullPath(args[1]);
int runSeconds = args.Length > 2 && int.TryParse(args[2], out var rs) ? rs : 6;
string outputMode = args.FirstOrDefault(a => a.StartsWith("--mode="))?[7..] ?? "WasapiShared";
bool dop = args.Contains("--dop");
Console.WriteLine($"[smoke] 模式={outputMode} DoP={dop}");

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

// 3. 连接共享内存与信号量（与主程序 IpcService 相同的握手）
MemoryMappedFile mmf;
MemoryMappedViewAccessor accessor;
for (int i = 0; ; i++)
{
    try
    {
        mmf = MemoryMappedFile.OpenExisting(IpcConstants.MmfName);
        accessor = mmf.CreateViewAccessor(0, IpcConstants.MmfSize);
        break;
    }
    catch
    {
        if (i > 100) { Console.WriteLine("[smoke] FAIL: MMF 未出现"); return 1; }
        Thread.Sleep(100);
    }
}
using var _a = accessor;
using var _m = mmf;
using var requestReady = Semaphore.OpenExisting(IpcConstants.RequestSemaphoreName);
using var responseReady = Semaphore.OpenExisting(IpcConstants.ResponseSemaphoreName);
using var notificationReady = Semaphore.OpenExisting(IpcConstants.NotificationSemaphoreName);
Console.WriteLine("[smoke] IPC 握手完成");

int lastNotificationVersion = accessor.ReadInt32(IpcConstants.NotificationVersionOffset);
using var transport = new MailboxClient(accessor, requestReady, responseReady);
byte[] responseBuffer = new byte[IpcConstants.MaxResponseSize];

MessageTypeId Send(CommandId cmd, ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> respPayload)
{
    var (type, length) = transport.RequestAsync(cmd, payload, responseBuffer, timeoutMs: 5000).GetAwaiter().GetResult();
    respPayload = responseBuffer.AsSpan(0, Math.Max(0, length));
    return type;
}

void PumpNotifications()
{
    int v = accessor.ReadInt32(IpcConstants.NotificationVersionOffset);
    if (v == lastNotificationVersion) return;
    lastNotificationVersion = v;
    long off = IpcEnvelope.NotificationSlotOffset(v);
    var typeId = IpcEnvelope.ReadMessageTypeId(accessor, off);
    Console.WriteLine($"[smoke] 通知: {typeId}");
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
    IsFadeEnabled = false,
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
    server.Kill(); server.WaitForExit(2000);
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

// seek 测试
Span<byte> posBuf = new byte[8];
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

server.Kill();
server.WaitForExit(3000);
Console.WriteLine("[smoke] 完成");
return 0;
