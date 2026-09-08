# AudioPlayer（BassPlayerSharp 的 C# 原生替代）

以 FFmpeg（库内 `Libraries\FFmpeg\x64` 同一套 DLL）+ 自研 WASAPI/ASIO 互操作实现的
独立播放进程，**NativeAOT 单文件发布，产物可直接替换 `Player\BassPlayerSharp.exe`**，
主程序零改动（IPC 协议逐字节兼容 `External\BassPlayerIpc.Shared`）。

参考实现：`D:\code\node\ECHO\native`（WASAPI 独占协商、DoP 打包与标记相位归一、
ASIO 采样率中转/缓冲候选/原生 DSD 扩展、预缓冲环形缓冲语义）。

## 构建

```
cd External\AudioPlayer
dotnet publish -c Release
# 产物: bin\Release\net11.0\win-x64\publish\BassPlayerSharp.exe（单文件 AOT）
```

发布产物运行时需要与 exe 同目录的 FFmpeg DLL（`avcodec-63/avformat-63/avutil-61/swresample-7`）。

## 部署（替换旧播放器）

1. 用 `publish\BassPlayerSharp.exe` 覆盖 `Player\BassPlayerSharp.exe`；
2. 删除 `Player\` 下的 bass 全家桶（`bass*.dll`、`tags.dll`）——不再需要；
3. FFmpeg DLL 无需处理：主程序输出根目录已有（`Libraries\FFmpeg\x64` 随主工程复制），与本进程共享同目录。

主程序通过 `Player\**` 通配复制部署该目录，进程名与 IPC 命名对象
（`BassPlayerSharp_SharedMemory` 等）与旧版完全一致，可直接替换验证/回滚。

## 架构（与 bass 的功能映射）

| bass 组件 | 本实现 |
|---|---|
| bass.dll 解码 + 格式插件 | FFmpeg.AutoGen：`Decode\PcmDecoder`（PCM 与 DSD→PCM 统一 float32，swresample 直出设备格式） |
| bassdsd（DSD→PCM） | FFmpeg DSD 解码器（输出率 = DSD/8）+ swr 到 DsdPcmFreq + 浮点域 DsdGain |
| bassdsd（DoP） | `Decode\DsdRawReader` 原始位流（镜像 FFmpeg repack 模型）+ `Playback\Ring.DopRing`（0x05/0xFA 标记渲染期重盖） |
| bassasio DSD Native | ASIO DSD 扩展（`ASIOFuture kAsioSetIoFormat`，LSB1/MSB1/NER8 写法全移植） |
| basswasapi 共享 | WASAPI 共享 + **GetMixFormat 精确初始化**（虚拟声卡常只接受混音格式）+ 会话音量 |
| basswasapi 独占 Push/Event | `Interop\WasapiOutput`：候选格式协商（PCM float32→24in32→16→32；DoP 24packed→24in32→32）、`AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` 对齐重试、Initialize 专用线程 3 秒超时（Thread.Join 不可内联） |
| bassasio | `Interop\AsioInterop/AsioHost`：注册表枚举、IASIO 裸虚表、缓冲候选、采样率中转、隐藏消息窗口 |
| bass_fx PeakEQ（10 段） | `Playback\Dsp.Equalizer`：RBJ 峰值滤波器（带宽 1.0 倍频程与 bass_fx 同曲线），原子快照无锁渲染 |
| DirectSound 输出 | 映射为 WASAPI 共享 + 软件增益（DirectSound 本就是共享 WASAPI 的封装） |

行为对齐要点：暂停=冻结（Stop/Start 保留环内数据）；换设备/模式保进度重建会话；
位流（DoP/NativeDSD）拒绝 EQ 并在 EqState 回滚；自然结束只发 PlayEnded；
首播 SetMusicUrl 预载 + PlayButton 无会话时自动开播；独占/ASIO 失败回退共享
（对应 bass 回退 DirectSound）。

新语义（按决策）：进度采样精确（anchor+played，替代旧的字节比例估算）；
淡入淡出全 PCM 模式采样精确斜坡（含换曲淡出的剩余<3s 跳过规则）；`.wv` 内嵌
DSD 降级为 PCM 解码（FFmpeg wavpack DSD 路径）。

## 验证状态（本机：Senary Audio 虚拟声卡 + 显示音频）

已实测：IPC 全协议、FLAC/真实 DSF 解码、共享模式连续播放与精确进度、seek、
暂停冻结/恢复、自然结束 PlayEnded、MusicEnd 复位、EQ 应用与位流拒绝、设备枚举、
独占 Push/Event 三格式协商（设备 48k-only 全拒→回退共享）、DoP 会话与协商、
ASIO 无驱动回退、NativeAOT 单文件发布。

待真实硬件验证：DoP/原生 DSD 位流实际出声（需独占声卡/DSD DAC）、ASIO 驱动矩阵。

## 工具

- `_tools\AudioPlayerSmokeTest`：IPC 冒烟测试客户端（模拟主程序驱动全部命令）。
  `dotnet run -- <exe> <音频> [秒] [--no-toggle] [--mode=输出模式] [--dop]`
- `_tools\RawWasapiProbe`：WASAPI 共享格式探针（诊断声卡接受哪些格式）。
