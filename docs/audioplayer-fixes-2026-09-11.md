# AudioPlayer 修复记录 — 2026-09-11

对应 [审查报告](audioplayer-review-2026-09-11.md) 的十项主要问题。产品代码、共享协议、UI 设置持久化、冒烟工具和 `Player/AudioPlayer.exe` 一起更新。

## IPC：消费确认与高频延迟

`MailboxClient` 使用单个后台发送线程。服务端对每条命令回显完整请求版本作为执行确认；收到确认并复制响应前不复用请求槽。UI 发布设置无需等待。音量、EQ、DSP 仅在相邻可合并状态段内保留同类最新值，Play、Seek、设备设置和响应型请求构成有序屏障。

发送超时只结束当前调用者等待；发送线程继续持有在途槽，直到执行确认或连接关闭。超时之后不再写调用者的响应缓冲，避免已归还的 ArrayPool 缓冲被迟到响应修改。进度轮询忙时直接跳过。

在本机使用独立进程、真实共享内存/信号量及生产服务端命令处理器，预热 200 次后测量 5000 次串行 ChangeVolume 执行确认往返：

| 形态 | 吞吐 | p50 | p95 | p99 | 最大值 |
|---|---:|---:|---:|---:|---:|
| JIT 最终回归 | 42,946 次/秒 | 0.021ms | 0.043ms | 0.070ms | 2.133ms |
| NativeAOT 回归 | 55,393 次/秒 | 0.016ms | 0.033ms | 0.050ms | 0.383ms |

这些结果支持当前机器上的 60–1000Hz 交互设置输入；不是硬实时或跨设备保证。文件打开、输出重建等长操作仍影响参数实际生效时刻，发送队列通过合并避免积压过时滑块值。测试未开启真实播放输出，因此没有把传输延迟等同于可闻音频响应延迟。

额外验证：

- 阻塞首条请求至超时后，连续发布 10001 次音量值，服务端最终只执行最新值；插在中间的 Play 顺序保持。
- 超时期间共享请求版本保持不变；服务端恢复后后续命令正常完成。
- 100 组 Settings → EQ → DSP → GetDspState 跨进程链路全部回读一致。
- 固定响应长度校验、非法载荷返回值和超长载荷拒绝统一处理。

## 播放与线程生命周期

- WASAPI 两条 Initialize 路径都记录超时 worker。Dispose 遇到未退出的初始化或渲染线程时，将原生对象移交后台回收线程；它们真正退出后才 Stop、Reset、Release 和关闭事件。
- PCM 和位流解码在 EOF 后都等待 seek；解码线程在 finally 中归还 decoder。控制端等待解码退出超时，不再丢失最终清理责任。
- seek 在解码前捕获环代数，Push 和 EOF 都验证该代数；环计数清零与消费计数更新在同一临界区。
- 满环生产者等待消费/seek/取消通知，移除 4ms 轮询。
- 新 Session 在发布给输出前安装软件增益与淡入初态；共享会话音量在设备 Start 前设置。

## 曲尾与进度

解码环为空只代表源数据已提交。`OutputDrainTracker` 继续按输出回调推进最后音频帧，直到设备管线排空才通知 PlayEnded。WASAPI 结合缓冲大小与流延迟；ASIO PCM/DoP 结合双缓冲与驱动报告的输出延迟，Native DSD 保留双缓冲估算。

进度由 Session 提交位置扣除设备待播管线，精度受回调周期和驱动延迟信息限制。UI 收到结束事件后先停表并固定末尾缓存，再在 UI 线程自动切曲；被取消的旧轮询不会正常继续写入缓存。快速 Stop/Start 创建新的轮询代次，避免旧任务尚未退出导致新轮询未启动。

因此，“曲尾进度继续动”的修复行为保留，同时不再通过提前停止输出截掉缓冲中的尾音。WASAPI Dispose 超时处理属于资源所有权问题，独立于曲尾进度逻辑。

## DSP、设备设置与清理

- EQ 控制线程只发布系数，渲染线程独占滤波历史，在块边界接受新系数；相同参数不重新分配快照。
- GainRamp 原子发布目标与时长，当前增益、步长和剩余帧只由渲染线程修改；连续 SetImmediately/RampTo 保留尚未消费的初值。
- DSP Sanitize 对有效设置直接返回原对象，相同 DSP 状态不重复发布。
- WASAPI endpoint ID 经设备枚举、UI 选择、SaveSettings、IPC 和输出解析完整传递。旧名称/索引设置在启动时迁移；名称缺失或重复时使用默认端点并记录诊断。分页第一页重新枚举，稳定 ID 不允许截断。
- 即便未设置 IsSettingChanged，只要活动会话的输出相关参数实际变化，也会按需重建。设置重建失败向 IPC 返回失败。
- 删除旧 SendOnly/FireCommand/响应轮询实现、过时邮箱状态字段、未使用 EQ 方法/属性、Session `_engine`/`_dopScratch`/`GainActive`、过时分页容量计算和 PlayMusic 未使用参数。
- 高频发送改为 stackalloc 序列化；WASAPI scratch 按 PCM/DoP 模式分配。修复 IPC 连接失败重试和关闭时的句柄清理，以及设备枚举/输出初始化的 COM 初始化配对。

## 验证与产物

- JIT 回归：137/137 通过。
- NativeAOT 回归：137/137 通过。测试反射需要的元数据仅在测试项目保留。
- 主程序 x64 Debug 最终编译通过（0 错误；仍有平台兼容、可空性和裁剪等编译警告）。
- 静音 WASAPI Shared 实机冒烟通过：6 秒文件自然结束并通知 PlayEnded，EOF 后 seek 到 5000ms，暂停 600ms 进度保持 5000ms，恢复后推进到 5500ms，MusicEnd 回到 0。测试工具按实际返回状态进入暂停，避免将 EOF 后的恢复误标为暂停。
- NativeAOT 播放器已发布，更新 `Player/AudioPlayer.exe`。
- 最终播放器 SHA256：`D32ED5C9183D17E4A0D351332B2DAC05960EE22104D30831C39BCBFD0D3E0F6E`。
- 日志：`_tools/audioplayer-fixes-regression.log`、`_tools/audioplayer-fixes-aot-tests.log`、`_tools/audioplayer-fixes-publish.log`、`_tools/audioplayer-fixes-ui-build.log`、`_tools/audioplayer-fixes-smoke.log`。

真实 DAC 的 ASIO/独占模式插拔和可闻尾音仍需要硬件验证。本次没有将静态分配优化或传输基准作为实际 GC pause 降低的证据。

实现参考：[IAudioClient.GetStreamLatency](https://learn.microsoft.com/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getstreamlatency)、[ASIO SDK 的延迟声明](https://github.com/kxproject/kx-audio-driver/blob/master/asio_sdk/asio.h)。
