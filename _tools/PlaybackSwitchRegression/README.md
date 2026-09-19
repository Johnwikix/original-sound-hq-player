# Playback switch regressions

`AtmosTests` 验证实验性 E-AC-3/JOC HDMI 直通：与 FFmpeg spdif 基准字节一致、
六声道音轨封装为 192 kHz 双通道载波、ring/输出保真、52 字节格式跨线程完整性、
seek/EOF 与默认关闭、共享输出临时切独占、ASIO 显式设备隔离。
`AtmosAutomaticTests` 覆盖暂停时开关和 seek、PCM 回退不重抢独占、失败状态协议兼容、
能力查询的线程与格式边界、真实 MMDevice 无效端点和 IPC 状态邮箱、设备丢失后的停止通知。
没有真实 HDMI/Atmos 设备出声验证。

`SurroundTests` 覆盖实验性 5.1 开关隔离、侧/后环绕布局、WASAPI 格式、ASIO 路由、
通道不足和复用边界；不依赖真实声卡。`AutomaticSurroundTests` 验证共享偏好下自动独占、
PCM 音量/静音、暂停切换、失败立体声回退、未知布局下混、真实 Windows 无效端点、
IPC v7/v8 兼容、ASIO 模式保留与 Atmos 优先级。`ProgressTests` 覆盖最新快照跨进程一致性、
时间戳外推、过期快照、连续 seek 确认、暂停/换曲及旧协议兼容。
仅运行进度测试：`dotnet run --project _tools/PlaybackSwitchRegression -- --test-progress`。

在 Windows、仓库根目录运行：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression
```

工具使用项目的 .NET 11 SDK、FFmpeg 包及仓库内 FFmpeg DLL。音频测试使用受控输出；IPC 测试启动独立测试进程，使用随机命名的共享内存和信号量。

默认测试包含合成 E-AC-3 5.1 M4A，验证随包 DLL 的实际解码能力、完整 PCM 时长、
有效样本、稳定 EOF、EOF 后 seek/回到开头，并覆盖原声道/立体声与源率/44.1 kHz。
用本地实际文件（包括 E-AC-3 Atmos/JOC）执行同一组无声卡测试：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression -- --test-pcm-file "D:\path\track.m4a"
```

该测试验证 PCM 兼容播放，不验证 Atmos 空间对象渲染或硬件出声。

导出回归直接编译生产 `FFmpegAudioConverter`，覆盖 E-AC-3 5.1(side) 与普通
立体声 WAV 到 WAV/FLAC/MP3/AAC/ALAC/OGG/Opus 的完整转换，再解码输出验证
声道数、时长、非静音及有限样本。MP3 下混为立体声，其余测试格式保留声道数。
这锁定了输入布局被改写导致 `Input changed`、MP3 被传入 5.1 导致编码器打开失败的问题。

```powershell
dotnet run --project _tools/PlaybackSwitchRegression -- --test-export-file "D:\path\track.m4a"
```

输出位于测试程序目录并在每项测试结束后清理，原文件只读。转换为这些格式不保留 Atmos 对象元数据。
失败返回退出码 1。DSD64 立体声静音 DSF 在工具输出目录自动生成；PCM 使用 `_tools` 下
44.1 / 48 / 96 kHz WAV。现有 `_tools/test_tone.dsf` 缺少头部 metadata pointer，不能作为
解码回归输入，因此此工具不依赖它。

测试编译真实 `PlaybackEngine`、`Session`、解码器和 WASAPI / ASIO 互操作代码。反射仅用于
避开引擎构造时的端点监听、看门狗，以及注入可控的原生虚表；格式选择和音源切换执行生产代码。

`ReviewFixTests.cs` 补充 PCM EOF/seek、陈旧解码块与 EOF 拒绝、满环唤醒、设备排空、
起播静音、Gain 并发与零分配、EQ 去重、稳定 endpoint ID、WASAPI 两类超时回收、
IPC 有序合并/超时恢复及跨进程延迟。另验证响度未知时的首块衰减、测量增益平滑和忙时缓存读取；当前 JIT/NativeAOT 均为 150 项。

覆盖：

- 旧会话仍存活时 PCM↔DSD 的新会话格式及播放进度。
- 连续 44.1→48→96→44.1 kHz PCM 解码和进度。
- 关闭位流后的 DSD→PCM，以及显式 Native DSD→DoP 回退。
- 同格式 PCM 复用，采样率/声道/位流/设备/输出模式变化及失效输出拒绝复用。
- ASIO 换率决策不能直接操作旧驱动与缓冲。
- WASAPI 渲染线程阻塞在 `GetCurrentPadding` 时，换率决策不能释放其在用的 COM 接口。

`4edbc4dc` 的回归来自三处：先开新会话却用旧会话的 `EffectiveKind` 选格式；ASIO
直接改采样率并保留旧缓冲；WASAPI 原地重初始化使用的 `_inRender` 哨兵未覆盖 padding
查询，而且检查暂停与进入临界区之间也不是原子的。修复限定为同格式 PCM 复用，格式变化
沿完整输出生命周期重建，同时按新 URL 选择解码路径。

这些检查可锁定软件回归，不能替代真实 DAC 对 ASIO / WASAPI 独占的出声测试。

`BufferPolicyTests.cs` 检查 ASIO 首选大小优先级，以及 WASAPI 共享直传/混音回退
两条路径传到原生 `IAudioClient.Initialize` 的缓冲时长（25/100/700ms）。
缓冲策略修复前新增用例中 8 项失败，修复后完整套件 92/92 通过。
AsioNotificationTests.cs 另行覆盖真实回调表通知、切歌拒绝旧输出、看门狗排队恢复、
暂停、取消过期计划、普通回调、停滞检测和释放竞争；完整套件现为 103 项。

## WavPack DSD

`WavPackTests.cs` 使用官方 DLL 的编码 API 在输出目录生成确定性的 WV 测试文件，
不依赖下载音频或额外的编码器程序。覆盖 DSD64/128、单/双声道、中文路径、LSBF 来源、
原始字节逐一比对、短缓冲完整帧读取、seek/EOF/重新打开、DoP 标记连续性及最后奇数字节帧。
同一文件还会通过真实 Session 验证 Native DSD / DoP 输出载荷、进度和 EOF 后 seek，
并检查 ASIO、WASAPI 独占、共享、关闭位流及 ASIO DoP 回退的路径选择。
普通 PCM WV 和损坏文件不会被识别为原始 DSD。

同一套回归也可发布为 NativeAOT，验证发布形态下的 DLL 加载和 C ABI：

```powershell
dotnet publish _tools/PlaybackSwitchRegression -c Release -r win-x64 -p:PublishAot=true -o artifacts/WavPack-regression-aot
.\artifacts\WavPack-regression-aot\PlaybackSwitchRegression.exe
```

## 每频段 Q

EqualizerQTests.cs 覆盖 81 字节 IPC 往返、旧 41 字节请求、截断请求、
Q 独立预设保存与缺失值回退、增益中心不变而带宽随 Q 改变、仅修改 Q 的播放端
更新、升降增益抵消，以及非法 Q 和低采样率的稳定性。完整套件 110 项。

`WasapiStallTests.cs` 使用受控原生虚表，验证运行中不重复 Start、恢复前开放渲染门控、Start/Stop 失败上报、三种 WASAPI 模式的停滞检测、正常暂停和恢复窗口，以及初始化对齐/格式失败后释放并重新激活客户端。
