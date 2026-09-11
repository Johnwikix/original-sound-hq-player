# AudioPlayer review — 2026-09-11

后续已按用户确认实施修复，当前状态见 [修复与验证记录](audioplayer-fixes-2026-09-11.md)。本文保留审查时的发现与证据。

范围：`External/AudioPlayer`、共享 IPC 协议、主程序 `Services/IpcService.cs` 及设置/播放调用链。未修改产品代码；新增无声卡诊断探针。优先级 P1 表示应优先修复的正确性/资源安全问题，P2 表示其他实质缺陷或性能问题。

## 结论与验证边界

当前最大的风险是命令传输的消费确认、播放生命周期与渲染线程的状态所有权。不能据此断言当前卡顿由 GC 引起。检查时没有运行中的 AudioPlayer/WinUIMusicPlayer，未采集真实播放的 GC pause、分配率、工作集或设备 underrun。

- 现有 `dotnet run --project _tools/PlaybackSwitchRegression --no-restore`：125/125 通过。NuGet 漏洞源读取出现 NU1900 警告。
- 新增 `dotnet run --project _tools/AudioPlayerReviewProbe --no-restore`：运行真实 Session/Decoder/共享邮箱和受控 WASAPI 虚表，不启动播放器服务、不使用物理输出。
- 诊断探针输出的是观测结果，不是“全部修复通过”的断言测试；输出见同目录 `results.txt`。
- 三项直接复现：PCM EOF 后不能 seek；三个连续邮箱命令只剩最后一个；WASAPI Initialize 超时仍进入 Stop/Reset。

## 主要发现

### 1. [P1] SendOnly 未等待消费，初始化和设置操作会相互覆盖

位置：`Services/IpcService.cs:339-351`，`InitializeMusic:88-95`；服务端 `PlayerIpcService.cs:152-173`。

`_sendLock` 只覆盖写入与发布版本，不覆盖服务端读取。服务端忙于打开文件、切换设备，或只是暂时未获得调度时，后续 SendOnly 会重写同一个槽。启动连续执行 UpdateEq、UpdateSettings、UpdateDsp 就有此窗口。探针连续发布三条后只读到 version=3 / UpdateDsp，前两条没有可恢复的存储位置。加上响应型请求也能覆盖尚未消费的设置。

更严重的是，服务端依次读 sequence、payload、command，没有原子快照保护。覆盖发生在读取期间时，可能拼出不同请求的头部和载荷；版本发布的内存屏障不提供读取期间的互斥。

建议：所有命令具备消费/执行确认，写入者在确认前不得复用槽；或者使用有界队列。高频滑块可以按参数键合并最后值，但 Play/Seek/设备切换不能跨命令类型合并。UpdateSettings/UpdateDsp 应返回 revision、实际应用状态及失败原因。仅让客户端开始等待响应不够，服务端当前明确不回复这些命令。

### 2. [P1] WASAPI Initialize 超时没有置位隔离标志

位置：`Interop/WasapiOutput.cs:352-372,589-612`。

`InitializeWithTimeout` 返回 EPending，却没有设置 `_initTimedOut`。共享直传和独占都走此路径。Start 失败后的 Dispose 因而仍调用 Stop/Reset/Release，而初始化 worker 可能还在使用同一个原生 client。故障驱动下可使超时保护再次卡死，或造成原生对象生命周期竞争。

探针实际结果：`result=0x8000000A, initTimedOut=False, Stop/Reset calls while worker active=2`。另一条 `InitializeNativeWithTimeout` 已设置标志，但不能保护这条常用途径。

建议：统一初始化工作项的所有权状态；超时后资源移交 worker/隔离容器，调用者不得再次操作或释放。worker 最终结束时再执行受控清理。

### 3. [P1] PCM EOF 后线程退出，后续 seek 永远无人处理

位置：`Playback/Session.cs:233-249`。

PCM 遇到 EOF 后 MarkInputEnded 并 break；DoP/DSD 则使用 WaitForSeekAfterEnd 等待重启。解码会领先设备播放，因此问题也会发生在曲尾仍有可听数据时回拖、暂停后回拖，或停止归零后恢复同一会话。

探针：EOF 时 `drained=True, decoderAlive=False`；RequestSeek(0) 后 `ready=0, decoderAlive=False`。BeginSession 已清空 InputEnded，因此新环既没有数据也不会再报告自然结束，可能持续静音。

建议：PCM EOF 同样保持解码线程存活，等待 seek 或取消；覆盖 EOF 前预解码完成、EOF 后 seek、停止后恢复三类测试。

### 4. [P1] 输出开始消费后才施加音量与淡入

位置：`Playback/PlaybackEngine.cs:302-346`；`Interop/WasapiOutput.cs:132-159`；`Playback/Session.cs:71`。

CreateOutput/Start 内部已经启动驱动和渲染，并等待至少两个渲染周期。直到它返回，引擎才 ApplyVolumeToOutput、Gain.SetImmediately(0)、设置淡入。新 GainRamp 默认是 1。DirectSound/独占/ASIO 的前几块 PCM 因此可能以单位增益入队，包括用户设为静音时；WasapiShared 的会话音量也在 Start 后才设置。已经入队的样本无法由后设的软件增益修正。

建议：将输出准备与启动拆开。在驱动 Start/第一块 Fill 前安装音量、EQ、DSP、淡入初态；复用输出时也要在发布新 source 前完成其增益配置。用记录首次 Fill 的假输出验证静音起播与低音量换曲。

### 5. [P2] 环形缓冲读空被当成设备已经播完

位置：`Playback/PlaybackEngine.cs:854-863`；`Playback/Ring.cs:40,143-146`。

FramesPlayed 在数据拷入设备待播缓冲时增加，IsDrained 只表示解码环已空。看门狗随后直接 Pause 并发 PlayEnded。若尾块仍在 WASAPI/ASIO 缓冲中，歌曲会提前停止/切歌；误差依赖实际缓冲时长与 50ms 看门狗相位。CurrentMs 同样表示提交进度，而不是设备已播放的采样位置。

建议：区分 source EOF、最后帧已提交、device drained；WASAPI 用 padding/音频时钟，ASIO 用驱动位置或已知管线延迟推进最终完成状态。测试末尾脉冲必须真正抵达输出后才能发 PlayEnded。

### 6. [P2] EQ 快照同时包含跨线程可变滤波历史

位置：`Playback/Dsp.cs:98-104,118,131-167`。

控制线程复制 old 数组中的 X/Y 历史，而渲染线程正逐样本更新同一数组。发布引用是原子的，但历史的八个字段不是一个原子快照。读取可横跨不同采样时刻，且发布前旧块还会继续推进；切到新数组时历史会回退或不一致。高 Q 热调时会产生瞬态风险，注释里的“不可变”和“状态连续”并不成立。

建议：控制线程只发布不可变系数；渲染线程独占历史，在块边界接收新系数，并决定保留/清空/过渡。这样也避免每次参数同步复制整份历史。

### 7. [P2] GainRamp 存在丢失更新，不只是一个块的可见性误差

位置：`Playback/Dsp.cs:190-205,212-231`。

RampTo 分开写 target 和 step，并读取由渲染线程修改的 current。Apply 则持有旧 step/target，并在达到旧目标时写 `_perSampleStep=0`。若新 RampTo 在此之前刚发布，新斜坡会被旧块清零；下一块可以处于 step=0、current!=target 的状态并永久保持错误增益，直到下一次控制操作。SetImmediately 的 current 也可能被在途 Apply 末尾覆盖。

建议：发布一份带版本的目标与时长；current、step、remainingFrames 只允许渲染线程修改。块边界计算新斜坡，渲染线程不回写控制端字段。

### 8. [P2] Seek epoch 只保护已经进入 Push 的数据

位置：`Playback/Session.cs:170-181,239-247`；`Playback/Ring.cs:76-82`。

如果解码线程已完成 HandleSeek 检查、正在 Read，控制线程这时 BeginSession 并投递新 seek，那么旧 Read 返回的数据进入 Push 时会采集到新的 epoch，于是旧位置音频被接受为新会话数据。解码线程下一轮 SeekToMs 后并不再次清环，因此旧块会混入新位置开头。

建议：解码前捕获代数，携带到 Push 并由环验证；或者把 seek、清环、更新锚点都交给解码线程按顺序完成。还需把 FramesPlayed 重置与在途 Render 计数纳入同一代数。

### 9. [P1] WASAPI 渲染线程 Join 超时后仍释放 COM 与事件

位置：`Interop/WasapiOutput.cs:589-612`。

Dispose 忽略 Join(2000) 的返回值。停止事件只能唤醒事件等待，不能中断驱动内的 GetCurrentPadding/GetBuffer/ReleaseBuffer。线程阻塞超过两秒时，后续 Stop/Reset/Release 和关闭事件仍执行；线程恢复后可能继续访问已经释放的对象或失效句柄。现有回归仅覆盖换格式时不立即重用 client，没有覆盖 Dispose 超时。

建议：渲染退出必须成为资源释放的前置条件。超时使用明确的延迟回收/隔离策略；统一幂等 Dispose，避免并行释放。ASIO 已有 `_renderUsers` 排空超时保留资源的模式可参考。

### 10. [P2] 设备列表永久缓存，设备选择却重新按当前枚举索引解析

位置：`PlayerIpcService.cs:290`；`Interop/WasapiInterop.cs:355-375`。

服务端首次分页枚举后永不刷新；播放时 ResolveDevicePtr 又重新枚举并按 int index 查找。插拔/禁用设备导致列表顺序变化时，界面旧名称对应的 id 可以选择另一台设备，甚至回落默认设备。重发设置本身不能解决这种身份漂移。

建议：IPC 传递稳定 endpoint ID，设备通知失效分页快照；分页协议携带枚举版本。设置响应应报告实际 endpoint、输出模式及回退原因。

## 性能、内存与 GC

### 可保留的设计

- 解码与输出分离，环容量有上限；PCM/DoP/DSD Session scratch 已部分按模式分配。
- PcmEffects 的目标发布、Span 处理及默认旁路已有测试；125 项回归包含默认/活动 DSP 渲染零分配验证。这只能证明被测 DSP 路径，不能等同整个播放器零分配。
- ASIO 同格式复用和回调排空已有针对性回归。独立音频进程隔离了 UI 托管堆，但自身仍会受该进程 GC 影响。

### 明确的分配与唤醒来源

1. `Ring.cs:104` 满环每 4ms 唤醒，消费者 Render 没有在腾出空间后 Pulse。暂停且环满时解码线程仍约每秒醒 250 次；3 秒钟不播放也会持续轮询。可在消费/seek/停止时通知，生产者以条件循环等待，并保留必要的故障超时。
2. `Session.cs` 各 DecodeProc 每次把实例方法 `Cancelled` 转成 Func，存在每块委托构造；缓存委托或改为显式取消状态。通常远小于 LOH 与解码本身，优先级低于正确性修复。
3. IPC 请求/响应/通知围绕 WaitOne 使用 Task.Run；客户端/服务端生存监控还有定时 Task.Delay。它们带来工作项、任务和唤醒，ArrayPool 并没有消除这部分。可统一到专用等待循环或注册等待，响应超时按单调时钟计算，并用版本状态而非仅依赖信号判断。
4. `Equalizer.RebuildSnapshot` 无参数去重，每次重新分配含历史的 Band[10] 并重算系数；DSP 设置在引擎与协议 sanitize 过程中也有快照对象。建议先按 revision/值去重，再按渲染线程所有权设计收敛快照。
5. WASAPI Start 同时分配 double PCM scratch 与 uint DoP scratch，两者实际只使用一种；Session 的 `_dopScratch` 未见使用。可按 RenderKind 分配并删除未使用缓冲。

### 容量量级（静态计算，非实测工作集）

默认 Latency=300ms 时 PCM 环约 0.6 秒，double 立体声每帧 16 字节；另有固定 16384×2×8 字节 decode scratch。

| PCM 采样率 | 环 + decode scratch，约 MiB |
|---|---:|
| 44.1 kHz | 0.65 |
| 192 kHz | 2.01 |
| 768 kHz | 7.28 |

未包含输出 scratch、FFmpeg 原生缓冲、线程栈、EQ、响度扫描及 CLR。换曲会先 OpenSession 再释放旧 Session，因此这部分峰值可以接近两份。多数数组属于大对象，频繁换曲/重建会增加 LOH 分配；不能直接由容量推断 GC pause。

响度分析另开解码器并完整扫描文件，与实时解码争用 CPU/I/O；全局 Gate 限制单个扫描，这是有益的。建议测量 DSD/高采样率扫描时的 underrun、CPU 和 GC，再决定调低优先级或限速。

Session.Dispose 在解码线程 1 秒内未退出时丢弃 decoder 引用；decoder 无终结回收逻辑。避免并发释放是对的，但若阻塞稍后恢复，资源仍没有最终归还路径。应让解码线程 finally 独占清理，并记录隔离会话数，避免反复切换慢盘/网络文件时原生资源累积。

## IPC 与结构的补充问题

- UpdateSettings/UpdateDsp 是无执行结果 API，UI 不知道失败或实际回退；UpdateSettings 还忽略载荷中的 IsEqualizerEnabled，只依赖另一条 EQ 命令。在命令可能覆盖的现状下，启动状态尤其不可靠。
- 通知是全局“最新一条”双缓冲，读取者只处理最新版本。这适合可覆盖的状态快照，不提供 PlayEnded 这类事件的逐条交付保证。状态应按类型独立，事件应有队列/确认。
- IpcEnvelope.ReadPayload 对非法长度返回 0，而服务端只检查 `<0`，错误分支不可达。客户端部分响应只按类型而不校验长度，就解析复用缓冲。应区分合法空载荷与损坏载荷，严格按命令尺寸校验并回传 InvalidPayload。
- `IpcService.Dispose` 未关闭 notification semaphore，也没有等待后台任务和发送队列退出；服务端 Dispose 同样没有停止后等待 listener/watchdog 排空。进程退出会最终回收句柄，但生命周期设计不支持安全的重复初始化/关闭。
- PlaybackEngine 公共可写字段、多个 Timer/Task 与 `_streamLock` 混合管理播放状态；看门狗读到旧 session 后进入锁，只复查 IsPlaying/旧 session.IsDrained，未校验该 session 仍是当前会话，可能对刚切换的新曲发旧结束事件。应在锁内同时确认 session/output 身份或 generation。
- 多处空 catch，以及入口统一 SetObserved，会掩盖输出失败、seek 失败和后台任务故障。用有限频率、结构化的诊断事件记录操作、generation、HRESULT、线程和恢复结果。
- 注释出现“命令永不覆盖”“不可变快照”“采样精确已播”等与实现不符的保证。优先修正契约；历史修复说明移到设计文档，方法注释保留当前前置条件、所有权和失败语义。

建议的职责边界：IpcTransport 负责可靠传输；命令执行器串行管理播放状态与 generation；Output 准备/启动/排空/销毁明确分阶段；Session 解码线程独占 decoder；渲染线程独占 DSP 历史与增益斜坡。无需为了“零分配”把一次性控制代码全部改成底层内存操作。

## 待验证的重采样问题

`PcmDecoder.cs:130` 每轮先以 null 输入调用 swr_convert。FFmpeg 文档把这种调用定义为转换末尾 flush；当前在正常输入之间也调用。6 秒测试音在 48k/96k 下分别得到 287983/575966 帧，按时长预计 288000/576000；原采样率输出完整。该观测不足以独立证明可听失真或排除时长/滤波延迟因素，所以未列为上述确定缺陷。

下一步应与同一 FFmpeg 版本只在最终 EOF flush 的参考流逐样本比较，并覆盖上采样、下采样、DSD→PCM 和连续块边界。参考：[FFmpeg libswresample](https://ffmpeg.org/doxygen/8.0/group__lswr.html)。

## 推荐修复与验证顺序

1. 可靠 IPC 确认、WASAPI 两类超时资源安全、PCM EOF 恢复、起播前安装增益。
2. 设备排空语义、seek generation、EQ/GainRamp 渲染线程所有权。
3. 稳定设备 ID、错误状态回传、生命周期排空，再处理空闲轮询与冗余分配。
4. 补充：暂停满环 30 秒、快速 EQ/DSP/设备设置、连续 seek、曲尾脉冲、模拟 Initialize/Render 卡住、快速切曲 100 次、物理 ASIO/WASAPI 插拔恢复。
5. 最后在真实 Debug/JIT 与发布 NativeAOT 分别采样；记录 AudioPlayer 自身分配率、GC pause、LOH、原生工作集、回调耗时与 underrun，避免把 UI 进程或完整 GC 周期时长误当作音频 STW。
