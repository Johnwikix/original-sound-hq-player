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
