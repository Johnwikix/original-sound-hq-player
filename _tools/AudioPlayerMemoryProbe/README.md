# AudioPlayer 内存采样

诊断程序编译真实 AudioPlayer 源码，保持 NativeAOT、`EventSourceSupport=false`，默认使用 `Interactive`（与当前生产入口一致）；只替换入口，增加每秒一次托管计数/GC 类型采样和测试专用 GC 请求事件。不替换 `Player/AudioPlayer.exe`，不连接用户正在运行的播放器。SmokeTest 使用独立 IPC scope、静音及真实 WASAPI 共享输出。

从仓库根目录执行（.NET 11 SDK 与现有依赖）：

```powershell
dotnet publish _tools/AudioPlayerMemoryProbe -c Release
./_tools/AudioPlayerMemoryProbe/Create-Fixture.ps1 -Path _tools/AudioPlayerMemoryProbe/bin/reference.wav
dotnet run --project _tools/AudioPlayerSmokeTest -- _tools/AudioPlayerMemoryProbe/bin/Release/net11.0/win-x64/publish/AudioPlayerMemoryProbe.exe _tools/AudioPlayerMemoryProbe/bin/reference.wav 12 --memory --scenario=baseline --vol=0
```

将 `baseline` 分别替换为 `gapless`、`compressor`、`rate025`、`rate5`，各次创建新进程。`switch` 使用 60 秒，每秒在 1× / 1.5× 之间切换。高采样率使用 `-SampleRate 192000 -Seconds 45` 生成另一份文件，跑 baseline / gapless。

- `[memory]` 列：毫秒、累计托管分配字节、`GetTotalMemory(false)`、最近 GC 的 committed 字节、最近 GC 的碎片字节、Gen0/1/2 次数。
- `[memory-process]` 列：阶段、Private Bytes、Working Set、句柄数、线程数。由客户端读取，避免进程枚举开销污染被测托管分配。
- `[memory-phase] stopped` 后保留当前会话以支持重播；约两秒后通过测试专用命名事件执行两次完整 GC，中间等待终结器。此步骤用于区分可回收垃圾和保留对象，**不属于正常播放行为或优化方案**。
- 采样任务自身也分配少量字符串、延迟任务。各组保持相同采样频率；不能把总分配率精确归属到某一生产方法。
- GC 之前的 heap 不等于存活对象量；committed 在首次 GC 前可能为零，不能据此认定没有托管堆。

独立方法测量：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression -- --measure-playback-memory
```

它对真实 FFmpeg 解码循环、倍速循环和压限循环预热后使用当前线程分配计数；卷积构造测量包括准备时的临时分配，不能直接当作长期存活量。它是 JIT 方法实验，进程级矩阵才是 NativeAOT + 真实输出实验。

结果：[分析报告](../../docs/audioplayer-memory-2026-09-27.md)、[数据摘要](Results-2026-09-27.csv)。

## 切歌和长时播放回归

使用至少 600 秒的文件，避免播放结束导致分配和 GC 被低估；`Create-Fixture.ps1 -Seconds 600` 可生成独立测试 WAV。两首路径不同、时长不同的同格式歌曲通过 `--next=<路径>` 交替播放。

```powershell
dotnet run --project _tools/AudioPlayerSmokeTest -- <探针.exe> <600秒歌曲.wav> 200 --memory --scenario=trackswitch --switch-count=200 --switch-interval-ms=2000 --next=<660秒歌曲.wav> --dev=1 --vol=0
dotnet run --project _tools/AudioPlayerSmokeTest -- <探针.exe> <600秒歌曲.wav> 300 --memory --scenario=baseline --dev=1 --vol=0
```

设备编号按当前活动端点选择。切歌测试要求每次 epoch 更新、进度推进且持续播放；连续播放要求进度每秒推进，提前停止则测试失败。`--switch-count` 与 `--switch-interval-ms` 分别控制次数及每次切换后的播放间隔。

- `[memory-phase] measuring` 到 `stopped` 为自然 GC 的统计窗口；手动 GC 数据必须排除。对照旧探针时使用同一客户端、歌曲、设备、GC 模式和采样周期，所有构建完成后串行运行。
- `[gc-pause]` 列：毫秒、累计 `GC.GetTotalPauseDuration()`、最近 GC 索引、最近 GC 的两个暂停段（毫秒）。每秒采样可能漏掉期间较早的 GC，暂停段最大值只能称观察值，不能当作全量最大值或 P99。
- `[memory-switch]` 列：已完成切歌次数、epoch、当前毫秒、总毫秒。
- 验证发布的 `Player/AudioPlayer.exe` 时添加 `--no-collect`，避免请求只存在于探针中的测试 GC 事件。独立测试发布 EXE 时，将其与项目内 FFmpeg/WavPack DLL 一同放进隔离测试目录，复现主程序打包后的布局。

原生内存及 HTTP 扫描取消回归：`dotnet run --project _tools/PlaybackSwitchRegression -- --test-audio-memory`。覆盖单/双/六声道环回绕、seek 代数、欠载静音、EOF 排空、100 次并发关闭，以及真实 FFmpeg 阻塞 HTTP 打开的取消和后续扫描。

## GC 延迟模式对照

用同一个已发布的探针，只改变 `AUDIOPLAYER_PROBE_LATENCY` 环境变量；所有构建完成后串行运行，每组启动独立进程。空值/`interactive` 为 `Interactive`，`sustained` 为 `SustainedLowLatency`，其他值直接报错。该变量仅诊断入口读取，生产入口不读取。

```powershell
$env:AUDIOPLAYER_PROBE_LATENCY = 'interactive'
dotnet run --project _tools/AudioPlayerSmokeTest -- <探针.exe> <600秒歌曲.wav> 200 --memory --scenario=trackswitch --switch-count=200 --switch-interval-ms=2000 --next=<660秒歌曲.wav> --dev=1 --vol=0
$env:AUDIOPLAYER_PROBE_LATENCY = 'sustained'
dotnet run --project _tools/AudioPlayerSmokeTest -- <探针.exe> <600秒歌曲.wav> 200 --memory --scenario=trackswitch --switch-count=200 --switch-interval-ms=2000 --next=<660秒歌曲.wav> --dev=1 --vol=0
Remove-Item Env:AUDIOPLAYER_PROBE_LATENCY
```

- `[gc-mode]` 记录实际 `LatencyMode` 和 `IsServerGC`，不能仅凭配置假定设置生效。
- `[gc-kind]` 列：毫秒、`GCKind`、GC 索引、代数、Concurrent、Compacted、HeapSizeBytes、PromotedBytes、FinalizationPendingCount、两个暂停段（毫秒）。每秒分别查询 Ephemeral/Background/FullBlocking，以保留不同种类最近的 GC；同一种类一秒内多次回收仍可能漏记，不作为全量事件追踪。
- 模式对照必须同时报告累计暂停、观察到的最长段、工作集、Private Bytes、托管 committed/碎片，以及实际后台/阻塞 Gen2。`SustainedLowLatency` 抑制常规前台 Gen2，并不禁止 Gen0/1、后台 Gen2、低内存或手动触发的完整回收；暂停改善不能推导出工作集下降。

## 2026-10-11 回退后的托管环对照（最终代码）

本地 PCM 环恢复修改前的托管存储；HTTP 原生环保持原有实现。保留两项 P2 修复：设备名称 PROPVARIANT 在 finally 释放、HTTP 响度扫描取消令牌传入解码器打开。生产使用标准 GC 和 Interactive，不在切歌或正常播放期间手动 GC；已重新发布 Player/AudioPlayer.exe。

同一份修复后的 NativeAOT 探针，依次通过环境变量请求 Interactive / SustainedLowLatency；两次实际模式均匹配请求，均为 Workstation GC。两首 WAV 长 600/660 秒、48 kHz 双声道、300ms 缓冲、静音、FFT/DSP 关闭、真实 WASAPI Shared 输出（Steam Streaming Speakers）。各切歌 200 次，间隔 2000ms，独立进程、同一客户端与采样频率，全部构建及其他回归完成后串行运行。每次切歌确认新 epoch 和进度，最终进度最小值分别为 1650/1640ms；均无提前结束。

| 最终托管环指标 | Interactive | SustainedLowLatency |
| --- | ---: | ---: |
| 完成切歌次数 | 200 | 200 |
| 采样窗口（s） | 401.712 | 401.806 |
| 累计 GC 暂停（ms，API） | 9.672 | 9.553 |
| 观察到的最长暂停段（ms） | 0.809 | 0.610 |
| 工作集平均（MiB） | 24.36 | 23.64 |
| 工作集采样峰值（MiB） | 26.70 | 24.35 |
| Private Bytes 采样峰值（MiB） | 16.43 | 15.01 |
| GC committed 采样峰值（MiB） | 6.383 | 6.379 |
| GC FragmentedBytes 采样峰值（MiB） | 4.79 | 4.67 |
| 托管分配率（KiB/s，含探针） | 510.96 | 510.83 |
| 自然 Gen0/Gen1/Gen2 | 51/50/50 | 51/50/50 |
| 观察到的后台/阻塞 Gen2 | 48/2 | 48/2 |

本轮 SustainedLowLatency 的数值略好，累计暂停差 0.119ms、分配率几乎相同；两组均只在早期出现两次阻塞 Gen2，其后为后台 Gen2。单次顺序对照不能证明延迟模式的稳定优势；生产仍保持 Interactive。与本次回退前的原生环标准 GC 实验相比，托管环的分配率更高，但自然回收由全部阻塞 Gen2 转为以后台 Gen2 为主，累计暂停更低；平均工作集仍为相近量级。

采样摘要见 [最终托管环数据](Results-2026-10-11-managed-latency.csv)。所有数字均限定为正常播放的采样窗口，排除停止后的手动 GC；最长段仅为每秒采样观察值，不是全量最大值或 P99。托管环探针 SHA256 为 `39F13D7F755F74F38E2FDCE09521379E641BCBC7D0C4DBB548F44204AEC1E09A`；重新发布的生产 EXE SHA256 为 `3D602BEB08F00FD4095125AD5FD351D55BDA81FF36E127490A66A8739773AF4E`。

最终代码通过 326 项完整回归、16 项 HTTP/DSD 回归；生产 EXE 通过真实 WASAPI 切歌、1.5× 倍速与压限无缝播放、暂停恢复及最终结束通知、FFT 开关生命周期检查。未执行真实 ASIO/独占硬件或长时间高采样率 DSD 性能测试。以下表格保留已回退方案的历史结果，不能作为当前生产实现的测量值。

## 2026-10-10 原生环实验（已回退）

.NET 11 RC NativeAOT、默认 GC、`Interactive`、WASAPI Shared（Steam Streaming Speakers）、48 kHz 双声道、300ms 缓冲、静音、FFT/DSP 关闭。两首 WAV 分别长 600/660 秒，200 次切歌间隔 2000ms；构建完成后串行对照，使用相同客户端、设备、歌曲和采样频率。基线为本次修改前保存的 NativeAOT 探针，改后探针使用当时的原生本地 PCM 环和 P2 修复；原生本地环随后已回退，以下为历史实验数据。入口每秒打印采样日志，分配率包含探针及 IPC 的分配；下表只使用正常播放窗口，排除末尾手动 GC。

| 指标 | 改前 200 次切歌 | 改后 200 次切歌 |
| --- | ---: | ---: |
| 托管分配率（KiB/s） | 509.99 | 281.93 |
| 工作集平均（MiB） | 23.87 | 23.83 |
| 工作集采样峰值（MiB） | 26.20 | 24.25 |
| Private Bytes 采样峰值（MiB） | 16.60 | 13.77 |
| 自然 Gen0/Gen1/Gen2 | 51/50/50 | 30/30/30 |
| 累计 GC 暂停（ms） | 9.074 | 16.510 |
| 观察到的最长暂停段（ms） | 0.503 | 1.409 |

分配率降低约 45%，Gen2 次数减少，平均工作集基本相同、采样峰值下降；本次累计暂停和观察到的最长暂停段反而增加，不能宣称原生环降低了 GC 暂停。每次切歌都确认新 epoch 和实际播放进度，改前/改后最小进度分别为 1650/1640ms。

改后连续播放 300 次一秒检查全部通过，采样窗口约 302 秒；分配率 15.84 KiB/s，工作集平均 20.09 MiB、采样峰值 22.61 MiB，自然 Gen0/1/2 均为 0、GC 累计暂停为 0。无 GC 的窗口内托管堆仍会增长，不能据此宣称零分配或长期工作集不增长。

原始数字见 [数据摘要](Results-2026-10-10.csv)。这是每组单次长时对照，暂停段为每秒采样观察值，未获得全量尾延迟或 P99；未执行真实 ASIO/独占设备和长时间高采样率 DSD 测量。

## 2026-10-10 暂停增长与延迟模式

回退前的原生 PCM 环代码使用同一个 NativeAOT 探针，串行各执行 200 次切歌，每次间隔 2 秒，采样窗口约 402 秒。默认 GC 为 Workstation，设置值由 `[gc-mode]` 确认。该实验没有改动生产入口的 `Interactive`。

| 标准 GC 指标 | Interactive | SustainedLowLatency |
| --- | ---: | ---: |
| 累计 GC 暂停（ms，API） | 15.064 | 16.613 |
| 观察到的最长暂停段（ms） | 1.161 | 1.195 |
| 自然 Gen0/Gen1/Gen2 | 30/30/30 | 30/30/30 |
| 观察到的阻塞/后台 Gen2 | 30/0 | 30/0 |
| 工作集平均（MiB） | 24.05 | 23.45 |
| 工作集采样峰值（MiB） | 25.98 | 23.82 |
| GC committed 采样峰值（MiB） | 5.31 | 5.31 |
| 托管分配率（KiB/s，含探针） | 285.02 | 285.02 |

模式设置生效，但本场景没有转换为后台 Gen2。补充 EventPipe 诊断构建（仅该构建设置 `EventSourceSupport=true`）在 30 次切歌期间采集 45 秒，捕获的自然 GC 均为 `AllocLarge` + `NonConcurrentGC`。PerHeapHistory 的 `CondemnReasons1=8192` 对应 `gen_gen2_too_small`，回收前 LOH 约 3.1–3.3 MiB；原生页不计入托管各代大小。系统检查时可用物理内存约 19 GiB，未发现 GC 环境变量覆盖。

本机编译包为 `runtime.win-x64.Microsoft.DotNet.ILCompiler/11.0.0-rc.1.26425.128`，其元数据提交为 `3551975be08744f0418857c5bed8ab1545c5dd47`。该提交的 [plan_phase.cpp](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/coreclr/gc/plan_phase.cpp#L1403) 使用 4 MiB 的 `bgc_min_per_heap`：Gen2、LOH、POH 均未超过阈值时选择阻塞 Gen2，没有对 SustainedLowLatency 豁免。[gcrecord.h](https://github.com/dotnet/dotnet/blob/3551975be08744f0418857c5bed8ab1545c5dd47/src/runtime/src/coreclr/gc/gcrecord.h#L59) 中原因位为 13，即 `1 << 13 = 8192`。

这解释了原生环减少分配后仍可能增加暂停：回收方式和单次成本改变，累计暂停并不与分配量或 GC 次数成正比。上一轮每秒暂停采样没有记录 GC 类型，不能还原其全部 GC 的具体原因；本轮类型记录和 EventPipe 证据确认了当前小堆阻塞分支。单次两组暂停差异不可直接解释为模式稳定优劣；SustainedLowLatency 的一般语义与内存代价见 [微软说明](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)。

### Satori 的模式和诊断语义

Satori 对照构建加入 `<Sdk Name="PublishWithSatoriGC" Version="11.0.0-satori.37965325036.1" />`，NativeAOT、EventSourceSupport、FFmpeg、业务源码和采样入口均保持相同；编译/链接响应文件确认采用该包的 `System.Private.CoreLib.dll`、`Runtime.WorkstationGC.lib` 与禁用 EventPipe 的库，而非仅添加未生效的 SDK 名称。CoreLib PDB 的 SourceLink 指向 `hez2010/Satori` 提交 `d818d4e1789b4a0e9b3c467deb499f116ab0b9c2`。

该版本的 [SatoriGC.cpp](https://github.com/hez2010/Satori/blob/d818d4e1789b4a0e9b3c467deb499f116ab0b9c2/src/coreclr/gc/satori/SatoriGC.cpp#L133) 将延迟模式值 `>=2` 映射到同一个低延迟开关：请求 `SustainedLowLatency` 后实际 getter 返回 `LowLatency`；`Interactive` 返回 `Interactive`。两组均返回 `IsServerGC=true`，这与标准 Workstation GC 的运行时实现不同，不能解释为使用了同一种 GC 配置。

[SatoriRecycler.h](https://github.com/hez2010/Satori/blob/d818d4e1789b4a0e9b3c467deb499f116ab0b9c2/src/coreclr/gc/satori/SatoriRecycler.h#L148) 中 `GetLastGcInfo(FullBlocking)` 返回最近 Gen2，`GetLastGcInfo(Background)` 返回最近任意 GC；因此会出现同一索引被多个 GCKind 返回，甚至 Background 查询返回 Gen1。统计必须按索引去重，并结合 Generation/Concurrent/Compacted 字段，不能直接按 GCKind 标签计数标准后台/阻塞 Gen2。

该版本 `GetMemoryInfo` 的 FragmentedBytes、PromotedBytes、PinnedObjectsCount、GenerationInfo 部分为占位零；`GetTotalMemory(false)` 返回的也是其 occupancy 口径。零值不能证明没有碎片或固定对象，托管 heap 数值也不宜直接与标准 GC 等同比较。跨 GC 的内存结论优先使用进程 Working Set、Private Bytes 和实测 committed；累计暂停仍明确标注来自各实现的 `GC.GetTotalPauseDuration()` API，最长段为每秒采样的观察值。

Satori 两组也使用回退前的原生本地 PCM 环，各完成 200 次切歌，每次间隔 2 秒；采用同一 Satori 探针，只改变模式请求。

| Satori 指标 | Interactive | 请求 SustainedLowLatency（实际 LowLatency） |
| --- | ---: | ---: |
| 累计 GC 暂停（ms，API） | 204.846 | 141.225 |
| 观察到的最长暂停段（ms） | 5.437 | 7.406 |
| 工作集平均（MiB） | 31.83 | 34.73 |
| 工作集采样峰值（MiB） | 35.07 | 38.28 |
| Private Bytes 采样峰值（MiB） | 28.98 | 54.63 |
| GC committed 采样峰值（MiB） | 18.84 | 44.59 |
| 托管分配率（KiB/s，含探针） | 319.32 | 317.42 |
| 自然 Gen0/Gen1/Gen2 | 398/398/199 | 398/398/199 |
| 去重观察到的并发/非并发 Gen2 | 199/0 | 199/0 |

本场景 Satori 的工作集和累计暂停均高于标准 GC；其低延迟模式相对 Satori Interactive 降低累计暂停，却增加观察到的最长暂停段和 committed，未采用到生产程序。不能将这个原生环实验直接当成回退后的托管环对照。四组历史结果见 [原生环模式对照](Results-2026-10-10-native-latency.csv)。
