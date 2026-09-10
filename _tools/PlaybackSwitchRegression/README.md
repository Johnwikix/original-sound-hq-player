# Playback switch regressions

在 Windows、仓库根目录运行：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression
```

工具使用项目的 .NET 11 SDK、FFmpeg 包及仓库内 FFmpeg DLL，不打开真实声卡或启动 IPC。
失败返回退出码 1。DSD64 立体声静音 DSF 在工具输出目录自动生成；PCM 使用 `_tools` 下
44.1 / 48 / 96 kHz WAV。现有 `_tools/test_tone.dsf` 缺少头部 metadata pointer，不能作为
解码回归输入，因此此工具不依赖它。

测试编译真实 `PlaybackEngine`、`Session`、解码器和 WASAPI / ASIO 互操作代码。反射仅用于
避开引擎构造时的端点监听、看门狗，以及注入可控的原生虚表；格式选择和音源切换执行生产代码。

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
