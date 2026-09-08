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
    CreateNoWindow = false,
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

byte seq = 0;
int requestVersion = accessor.ReadInt32(IpcConstants.RequestVersionOffset);
int lastNotificationVersion = accessor.ReadInt32(IpcConstants.NotificationVersionOffset);

bool IsSendOnly(CommandId cmd) => cmd is CommandId.Play or CommandId.ChangePosition
    or CommandId.ChangeVolume or CommandId.MusicEnd or CommandId.FadeOut or CommandId.UpdateSettings;

MessageTypeId Send(CommandId cmd, ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> respPayload)
{
    seq++;
    IpcEnvelope.WriteCommand(accessor, IpcConstants.RequestBufferOffset, cmd, seq, payload);
    requestVersion++;
    IpcEnvelope.PublishVersion(accessor, IpcConstants.RequestVersionOffset, requestVersion);
    requestReady.WaitOne(0); // 清掉可能残留的计数（服务端忙于上一条时不会等待）
    requestReady.Release();
    if (IsSendOnly(cmd))
    {
        respPayload = default;
        PumpNotificationsQuiet();
        return MessageTypeId.Success; // send-only：与主程序一致，不等待
    }
    if (!responseReady.WaitOne(5000))
    {
        respPayload = default;
        return MessageTypeId.Failed;
    }
    var typeId = IpcEnvelope.ReadMessageTypeId(accessor, IpcConstants.ResponseBufferOffset);
    byte rspSeq = IpcEnvelope.ReadSequenceId(accessor, IpcConstants.ResponseBufferOffset);
    int len = IpcEnvelope.ReadPayloadLength(accessor, IpcConstants.ResponseBufferOffset);
    byte[] buf = new byte[len];
    if (len > 0) accessor.ReadArray(IpcConstants.ResponseBufferOffset + IpcConstants.EnvelopeHeaderSize, buf, 0, len);
    respPayload = buf;
    return rspSeq == seq ? typeId : MessageTypeId.Failed;
}

void PumpNotificationsQuiet()
{
    int v = accessor.ReadInt32(IpcConstants.NotificationVersionOffset);
    if (v != lastNotificationVersion) lastNotificationVersion = v;
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
int setLen = BinarySerializer.WriteIpcSetting(setBuf, new IpcSetting
{
    OutputMode = outputMode,
    BassOutputDeviceId = -1,
    Latency = 300,
    IsDopEnabled = dop,
    DsdGain = 6,
    DsdPcmFreq = 88200,
    IsEqualizerEnabled = false,
    Volume = 0.15f,
    IsSettingChanged = false,
    IsFadeEnabled = false,
});
t = Send(CommandId.UpdateSettings, setBuf[..setLen], out _);
Console.WriteLine($"[smoke] UpdateSettings → {t}");

Span<byte> playBuf = new byte[BinarySerializer.PlayRequestSize];
BinarySerializer.WritePlayRequest(playBuf, new PlayRequest { Url = mediaPath });
t = Send(CommandId.Play, playBuf, out _);
Console.WriteLine($"[smoke] Play(send-only) → {t}（send-only 无响应为正常）");
Thread.Sleep(400); // 给服务端完成会话建立（邮箱协议按版本取最新，连发会跳过中间命令）
PumpNotifications();

// 播放按钮确认状态（--no-toggle 时跳过，观察连续播放进度）
if (!args.Contains("--no-toggle"))
{
    t = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var ps);
    Console.WriteLine($"[smoke] PlayButton → {t}" + (ps.Length >= 1 ? $" payload={ps[0]}（1=播放中）" : " (载荷异常)"));
    PumpNotifications();
}

// EQ 全 +10dB（验证 EqState 双向）
Span<byte> eqBuf = new byte[BinarySerializer.UpdateEqRequestSize];
BinarySerializer.WriteUpdateEqRequest(eqBuf, new UpdateEqRequest
{
    IsEnabled = true,
    Band0 = 10, Band1 = 8, Band2 = 6, Band3 = 4, Band4 = 2,
    Band5 = 0, Band6 = -2, Band7 = -4, Band8 = -6, Band9 = -8,
});
t = Send(CommandId.UpdateEq, eqBuf, out var eq);
Console.WriteLine($"[smoke] UpdateEq → {t} len={eq.Length}" + (eq.Length >= 2 ? $" enabled={eq[0]} active={eq[1]}" : " (载荷异常)"));

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

// 暂停→恢复
t = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var ps2);
Console.WriteLine($"[smoke] PlayButton(暂停) → {t}" + (ps2.Length >= 1 ? $" payload={ps2[0]}（0=已暂停）" : " (载荷异常)"));
Thread.Sleep(600);
t = Send(CommandId.GetTimeProgress, ReadOnlySpan<byte>.Empty, out var prog3);
if (t == MessageTypeId.TimeProgress)
{
    var (curMs3, _) = BinarySerializer.ReadTimeProgress(prog3);
    Console.WriteLine($"[smoke] 暂停期间进度 {curMs3 / 1000.0:F1}s（应冻结）");
}
t = Send(CommandId.PlayButton, ReadOnlySpan<byte>.Empty, out var ps3);
Console.WriteLine($"[smoke] PlayButton(恢复) → {t}" + (ps3.Length >= 1 ? $" payload={ps3[0]}（1=播放中）" : " (载荷异常)"));

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
