External/AudioPlayer 代码 review 修复计划（6 项修复 + 清理，约 60 行，无架构变化）

正确性（blocking）：
1. PushDop 字节序按 DoP 标准 v1.0 修正：first DSD 字节占 bit15-8（当前在 bit7-0，DAC 会解出噪声）
2. FrameRingBase 加会话代数（BeginSession 递增），Push 写入前比对，seek 重置后中止旧数据写入（消除 seek 毛刺）
3. WasapiOutput Initialize 超时路径：加 _initTimedOut 标志，Dispose 跳过该 client 的 Stop/Reset/Release（ECHO 墓园语义，避免与挂死线程并发操作 COM 对象）

健壮性（important）：
4. AsioHost.TryCreateBuffers 失败路径补 DisposeBuffers（否则候选循环失效）
5. WatchdogTick 轮询 output.IsFailed：设备中途失效时按暂停处理并通知 PlayStateUpdate(false)
6. StartOutputAndPlay 失败出口补发 PlayStateUpdate(false)（纠正应用侧乐观 UI）

清理：
- Ring.Push 无意义 Pulse、_stopVersion 死字段、toFree 死参数、WaitForRate 重复重载、CurrentPacketBytePos 死代码、ReadInterleaved 返回值钳制、EQ 多声道直通注释

验证：Debug 构建 + NativeAOT 发布 + 冒烟回归（含 seek 路径），部署到 Player/、bin、AppX 三处