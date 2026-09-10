# AudioPlayer — 独立音频播放进程

原音 HQ 播放器的播放引擎，一个独立于 UI 的 AOT 单文件进程：FFmpeg 解码 +
自研 WASAPI/ASIO 互操作，与主程序经 `External\BassPlayerIpc.Shared` 的
IPC 契约通信（信封/序列化逐字节兼容；命名对象 `AudioPlayer_SharedMemory` /
`AudioPlayer_RequestReady` / `AudioPlayer_ResponseReady` /
`AudioPlayer_NotificationReady` / `AudioPlayer_SingleInstanceMutex`，
客户端存活互斥体 `WinUIMusicPlayer_SingleInstanceMutex`）。

前身是 bass.dll 系（bass/basswasapi/bassasio/bassdsd/bass_fx），已完全移除；
架构上仍保留与 bass IPC 面的逐一对应（见文末映射表），便于行为对照。

## 架构

```
AudioPlayer.exe（NativeAOT 单文件，win-x64）
├── Program.cs                入口：SustainedLowLatency + timeBeginPeriod(1) + IPC 服务
├── PlayerIpcService.cs       MMF + 信号量 IPC 面（命令/响应/通知三通道）
├── Decode/
│   ├── PcmDecoder.cs         FFmpeg 解码 → swresample → float64 交织
│   │                         （PCM 与 DSD→PCM 统一路径；DSD 输出率 = DSD/8，可重采样到 DsdPcmFreq）
│   └── DsdRawReader.cs       DSDIFF/DSF 原始位流读取（DoP / Native DSD 用）
├── Playback/
│   ├── PlaybackEngine.cs     引擎：会话生命周期、输出模式、看门狗自愈、独占换曲复用
│   ├── Session.cs            播放会话：解码线程 + 环形缓冲 + EQ/增益（IRenderSource）
│   ├── Ring.cs               SPSC 帧环（会话代数防 seek 串音；PcmRing/DopRing/DsdByteRing）
│   ├── Dsp.cs                十段峰值 EQ（RBJ，全 double）+ 采样精确增益斜坡
│   └── RenderSource.cs       渲染接口与输出模式定义
├── Interop/
│   ├── WasapiInterop.cs      裸 COM 虚表 WASAPI/设备枚举/端点通知
│   ├── WasapiOutput.cs       共享（AUTOCONVERTPCM 直传）与独占 Push/Event
│   ├── AsioInterop.cs        IASIO 裸虚表 + SDK 结构
│   ├── AsioHost.cs           ASIO 宿主：缓冲候选/采样率中转/隐藏消息窗口/原生 DSD
│   └── Win32.cs              内核对象/注册表/窗口等 P/Invoke
└── Diagnostics/BufferDump.cs AP_DUMP 环境变量门控的端点字节转储（64MB 上限）
```

## 音频管线

**全程 float64**：解码（swr `AV_SAMPLE_FMT_DBL`）→ `PcmRing`（double 交织）→
EQ/音量/淡入淡出（double 域）→ 出口按设备格式转换（float32/16/24/32 整型、
DoP、DSD 位流）。中间处理噪声低于 -140dBFS，24bit 源全链路位透明。

- **PCM**：FFmpeg 解码，采样精确进度（anchor + 环已播帧）
- **DoP**：`DsdRawReader` 原始位流 → uint32 采样，0x05/0xFA 标记按**全局渲染帧
  计数**交替（奇数缓冲边界不翻相），静音 payload 0x6969
- **Native DSD**：ASIO future 扩展（kAsioSetIoFormat 0x23111961 系魔数；厂商
  私有返回值如 FiiO 0x3F4847A0 按 SDK 语义判定），LSB1/MSB1/NER8 写法全支持，
  采样率域自动尝试位率/字节率两种

**EQ**：RBJ 峰值滤波器（带宽 1.0 倍频程，中心 32Hz~16kHz），系数与滤波器状态
全程 double，参数变化时重算并原子换快照（渲染线程无锁只读）；位流会话
（DoP/NativeDSD）拒绝 EQ（EqState 回滚语义）。**增益斜坡**：采样精确线性
300ms（音量变化防 zipper + 淡入淡出），WasapiShared 稳态增益回 1（音量由
会话音量承担，不叠加）。

## 输出模式

| 模式串 | 实现 |
|---|---|
| `DirectSound` / `WasapiShared` | WASAPI 共享，**AUTOCONVERTPCM 直传源格式**：采样率/声道转换交给音频引擎，系统改"输出音频格式"不再需要会话重建 |
| `WasapiExclusivePush` / `WasapiExclusiveEvent` | 格式候选协商（PCM: float32→24in32→16→32；DoP: 24packed→24in32→32）、`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` 对齐重试、MMCSS Pro Audio 渲染线程、Initialize 3 秒超时墓园语义 |
| `ASIO` | 注册表枚举驱动、STA 消息窗口线程（防驱动自死锁）、缓冲候选枚举、采样率中转（含 pivot 尝试）、通道类型校验 |

**端点跟随**（共享模式）：监听默认设备切换/端点禁用拔出/系统格式变化，
播放中静默换输出（保解码环与进度），暂停中丢弃旧输出待恢复时落新设备。

**独占换曲复用**（ASIO 与 WASAPI 独占，PCM↔PCM 同设备）：保活输出只换渲染
源——同格式零设备交互无缝交接；ASIO 换率 Stop→SetSampleRate→Start（缓冲不
重建）；WASAPI 独占换格式保设备指针/渲染线程重激活重协商。位流/设备变化仍
全量重建。

**看门狗自愈**：输出失效按 +1s/+3s/+7s 计划自动重建会话（保进度），用户操作
即刻作废未决计划；连续 <4s 快速失败两次即暂停自愈（防错误循环刷屏），UI
不随暂态失效闪烁。

**失败回退**：独占/ASIO 打不开 → 回退 WASAPI 共享（会话强制 PCM 保进度重建）；
ASIO 原生 DSD 协商失败 → 先降级 ASIO DoP，再退共享 PCM。

## 构建

前置：.NET 11 SDK（仓库 `global.json` 锁定 11.0 RC1 系列）。

```
cd External\AudioPlayer
dotnet publish -c Release -p:Platform=x64
# 产物: bin\x64\Release\net11.0\win-x64\publish\AudioPlayer.exe（单文件 AOT）
```

运行依赖与 exe 同目录的 FFmpeg DLL（`avcodec-63 / avformat-63 / avutil-61 /
swresample-7`）——FFmpeg.AutoGen 默认按 exe 所在目录定位，无需任何路径适配。

## 部署布局

应用根目录**平铺**：主程序 exe、`AudioPlayer.exe`、一套 FFmpeg DLL 同目录
（全程序唯一样本，两个进程共用）。

- 仓库内 `Player\AudioPlayer.exe` 是发布产物暂存（发布时 `-o Player` 覆盖）；
- 主工程 csproj 把 `Player\*.exe`（及 `*.dll`）与 `Libraries\FFmpeg\x64\*.dll`
  经 `<Link>%(Filename)%(Extension)</Link>` 映射到输出根目录，构建即部署。

## 验证状态

冒烟全链路（`AudioPlayerSmokeTest`）+ 端点字节转储数学验证（`_tools\analysis`）
已通过：正弦 rms/Goertzel/过零与理论吻合、EQ 开启实测增益与 RBJ-double 传输
函数小数点后四位吻合、DoP 标记全程严格交替且载荷与 .dff 源零失配、ASIO
Int32LSB 与 Native DSD（MSB1）位流逐字节精确。

真实硬件（FiiO KA13）：ASIO/独占 DoP/原生 DSD 出声、系统格式变更后自动恢复
均已实测。

## 工具（`_tools\`）

| 工具 | 用途 |
|---|---|
| `AudioPlayerSmokeTest` | IPC 冒烟客户端：`dotnet run -- <exe> <音频> [秒] [--mode=输出模式] [--dop] [--dev=N] [--vol=F] [--no-toggle] [--no-eq] [--devices-first] [--trackchange]` |
| `analysis/*.py` | 转储验证器：`gen_sine.py`（标准正弦源）、`verify_sine.py`（Goertzel+rms+过零）、`verify_dop.py`（DoP 标记相位+载荷比对）、`verify_dsd.py`（DSD 位流比对） |
| `AsioProbe` | 驱动级 IASIO 探针（通道类型/DSD 扩展/采样率域） |
| `RawWasapiProbe` | WASAPI 共享格式探针（声卡接受哪些格式） |
| `WasapiPushProbe` | 独占 Push 模式行为探针 |
| `FormatSwitch` | 系统输出格式读写（验证格式变更自愈） |

转储开关：设 `AP_DUMP=<路径>` 启用端点字节转储（`AP_DUMP_MAX` 上限字节）。

## 参考

- ECHO（`D:\code\node\ECHO\native`）：WASAPI 独占协商、DoP 打包与标记相位归一、
  ASIO 采样率中转/缓冲候选/原生 DSD 扩展、预缓冲环形缓冲语义的参考实现。
- 与 bass 的功能映射（历史对照）：

| bass 组件 | 本实现 |
|---|---|
| bass.dll 解码 + 格式插件 | `Decode\PcmDecoder`（FFmpeg，float64 统一管线） |
| bassdsd（DSD→PCM） | FFmpeg DSD 解码器 + swr 到 DsdPcmFreq + double 域 DsdGain |
| bassdsd（DoP） | `Decode\DsdRawReader` + `Playback\Ring.DopRing`（全局帧计数标记） |
| bassasio DSD Native | ASIO DSD 扩展（`ASIOFuture kAsioSetIoFormat`，LSB1/MSB1/NER8） |
| basswasapi 共享 | WASAPI 共享 + AUTOCONVERTPCM 直传 + 会话音量 + 端点跟随 |
| basswasapi 独占 Push/Event | `Interop\WasapiOutput`：候选协商/对齐重试/初始化超时墓园 |
| bassasio | `Interop\AsioInterop/AsioHost`：裸虚表 + 缓冲候选 + 采样率中转 + 消息窗口 |
| bass_fx PeakEQ | `Playback\Dsp.Equalizer`：RBJ double，原子快照无锁渲染 |
| DirectSound 输出 | 映射为共享直传路径（DirectSound 本就是共享 WASAPI 的封装） |
