# ASIO 驱动面板修改缓冲后的播放恢复调研

日期：2026-09-10。

## 结论与本次实现范围

可以通过关闭并重新打开 ASIO 输出恢复播放，无需用户重启 AudioPlayer 进程。
这会短暂中断音频，不能保证无缝。实际恢复时间和是否需要完整重载驱动取决于厂商实现。

本次已实现：初始化时，在 PCM/DSD 和采样率协商完成后重新读取驱动首选缓冲大小，
优先创建该大小的缓冲；同时让 DirectSound / WASAPI 共享使用传入的 Latency。
播放中驱动通知触发的自动重建属于本次调研范围，尚未实现，因此仅更新缓冲选择策略
不会消除驱动面板修改后的停播。

## 协议与现有实现的证据

Steinberg SDK 的 `kAsioResetRequest` 说明要求宿主关闭并重新初始化驱动，明确包括
控制面板配置变化。早期 SDK 把 `kAsioBufferSizeChange` 标为未支持，要求改用 reset；
因此不能只监听 buffer-size-change。`kAsioLatenciesChanged` 则用于重新读取延迟，
不等同于缓冲大小变更。[Steinberg SDK 头文件（kX 工程保留副本）](https://github.com/kxproject/kx-audio-driver/blob/master/asio_sdk/asio.h)

JUCE 的 ASIO 宿主对 reset、buffer-size-change 和 resync 安排延迟重启；回调只
安排工作，随后在定时器中关闭、重新打开，再按原播放状态启动。读取缓冲参数时也会
跟随已改变的 preferred。它还等待自身打开的模态控制面板退出后再执行重建。
这里只用它核实行为，没有引入其源码或依赖。[JUCE ASIO 实现](https://github.com/juce-framework/JUCE/blob/master/modules/juce_audio_devices/native/juce_ASIO_windows.cpp)

本项目 `AsioHost.OnAsioMessage` 对 reset/resync 返回 1，但不设置待处理状态，
`OnSampleRateChanged` 为空。驱动停止回调后，`RenderSafely` 不再执行，也不会产生异常，
所以 `IsFailed` 保持 false。`PlaybackEngine.WatchdogTick` 仅检查 IsFailed 和自然结束，
不会启动恢复。该缺口与“进度停止、手动重建恢复”的现象一致；具体硬件发送的 selector
尚需实机日志确认。

## 建议接入流程

1. 驱动回调只原子记录重置原因、时间和目标实例；禁止在回调里 Stop、Dispose、
   等待锁或 CreateBuffers。reset/buffer-size-change/resync 进入恢复队列；延迟变化
   单独刷新延迟。采样率通知需区分主动协商和运行中外部变化，避免自己触发无限重建。
2. 控制线程合并连续通知，持 `_streamLock` 核验输出实例和 `_playGen`，保存播放位置、
   暂停状态与当前 RenderKind。用户换曲、暂停或停播应取消或改变待执行恢复。
3. Stop 后确保在途回调退出，再释放缓冲和驱动，重新创建驱动对象。现有 Dispose
   会清空静态 `_active`，但已经取得旧引用的回调仍可能在执行，需补齐生命周期同步；
   不可仅把 `_failed` 设为 1 就宣称完整解决。
4. 重新设置 PCM/DSD 格式与采样率，然后读取 min/max/preferred/granularity，
   创建首选缓冲并重建对应 scratch，读取新的设备延迟。已有缓冲选择策略可直接复用。
5. 保留解码会话，或按保存位置及原 RenderKind 重建会话；原本播放才 Start，原本暂停
   则保持暂停。纯缓冲变化不应意外切换 Native DSD、DoP 或 PCM。
6. 驱动暂时忙时有限重试，区分用户配置变更与真实设备故障，避免触发现有快速失败
   计数后过早停机。现有恢复计划可复用部分逻辑，但需处理过期通知和定时器重入。

本项目隐藏窗口线程专门服务驱动消息；现有注释记录了部分驱动同步等待该窗口响应。
因此重建不能随意搬到窗口消息泵线程，需保留独立的驱动控制执行路径。

若驱动完全不发通知，可增加“正在播放且回调长时间不再到达”的检测作为兜底；暂停、
初始化、正常 EOF 和重建期间必须排除。阈值应根据当前缓冲周期确定，不能误判正常大缓冲。

## 验证边界

本次缓冲策略用受控原生虚表验证 WASAPI 两条 Initialize 路径收到 25/100/700ms，
并检查 ASIO 256/1024/2048 首选值排在第一位。自动恢复还需覆盖真实 reset 回调到
控制线程重建的完整链路、连续通知合并、暂停/换曲竞争、旧回调退出及失败重试。
最后在实际 DAC 上测试 PCM、DoP、Native DSD 下 512→1024→2048，以及修改后继续
切换采样率。软件模拟通过不能替代硬件出声确认。

共享模式 Latency 是缓冲容量请求，并非精确的端到端延迟；音频引擎可分配更大缓冲，
最终以 GetBufferSize 返回值为准。[Microsoft IAudioClient.Initialize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)
