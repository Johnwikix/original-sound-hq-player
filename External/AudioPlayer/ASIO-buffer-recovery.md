# ASIO 驱动配置变更后的恢复

日期：2026-09-10。DirectSound / WASAPI 共享保留各自现有行为。

## 已实现

- ASIO 每次初始化在格式、采样率协商后读取驱动首选缓冲大小，优先使用。
- 完整初始化持久回调表，同时注册普通 bufferSwitch 和带时间信息的回调。
  原先用未清零内存分配回调表，普通 bufferSwitch 字段没有赋值，可能含随机地址。
- reset、buffer-size-change、resync 以及实际采样率变化立即标记输出需重建；
  同率通知不触发循环重建。回调只记录状态，不调用 Stop 或释放驱动。
- 待重建输出拒绝同格式切歌复用。看门狗合并 300ms 内连续通知，释放旧输出后
  延迟 250ms 安排恢复；重新读取首选缓冲，保留进度和原 RenderKind。
- 主动配置变更不累计快速设备失败次数。暂停时不自动起播，恢复播放时重建；
  新的用户操作取消旧恢复计划。失败重试使用当前时刻加退避间隔。
- 正在运行但无回调超过 max(2 秒, 4 倍估算双缓冲时长) 时标记失效，进入现有恢复。
  暂停排除该检测，运行中重复 Resume 不再重复 Start 或重置停滞计时。
- 释放输出前阻止新渲染，等待在途回调退出再 Stop / DisposeBuffers / Release。
  在途回调超时则保留驱动资源，避免释放仍被使用的指针。

## 协议与实现参考

Steinberg SDK 的 kAsioResetRequest 要求宿主关闭并重新初始化驱动，包括控制面板
配置变化。早期 SDK 的 buffer-size-change 约定尚未启用，要求使用 reset，所以
不能只监听 buffer-size-change。latencies-changed 是重新读取延迟的通知，与
缓冲重置不同。目前本项目延迟仍为缓冲估算值，精确硬件延迟刷新未在本次加入。
[SDK 头文件（kX 工程保留的 Steinberg 源码）](https://github.com/kxproject/kx-audio-driver/blob/master/asio_sdk/asio.h)

JUCE 对 reset、buffer-size-change、resync 安排回调之外的延迟重建。这里只核实
行为，没有引入其源码或依赖。
[JUCE ASIO 实现](https://github.com/juce-framework/JUCE/blob/master/modules/juce_audio_devices/native/juce_ASIO_windows.cpp)

本项目隐藏窗口线程用于服务驱动同步消息；驱动控制调用继续在调用方执行，不能
随意搬到消息泵线程，否则部分驱动可能等待自身消息而死锁。

## 验证边界

AsioNotificationTests 使用模拟原生驱动捕获真实 CreateBuffers 回调表，直接发送
通知，再调用真实切歌复用决策与 WatchdogTick。覆盖通知失效、同率通知、两种音频
回调、无回调检测、暂停、排队恢复、取消过期恢复、在途回调与释放竞争。
修复前复现通知后旧输出仍被复用，以及在途渲染期间释放驱动缓冲。

这些测试锁定软件路径，不能证明特定 DAC 已正常出声。实机需验证播放中修改驱动
采样率/缓冲后恢复、立即切同采样率或不同采样率歌曲、暂停后修改再恢复，以及
PCM / DoP / Native DSD。设备重开会短暂断音，实际时间取决于驱动。
