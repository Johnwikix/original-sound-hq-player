# 本地 FLAC 曲尾停住排查（2026-09-23）

## 结论

基线为 `c105fd2b`（1.2.2.0），对照到 `96cb8e82`。确定引入本例回归的是
`636f4844`（2026-09-21，audioplayer 添加真实的网络播放能力）。

该提交为了让网络读取失败、断流和取消进入失败状态，避免误触发自然结束及下一曲，
同时修改了共用的 PCM 解码错误处理。本地 FLAC 曲尾的 `AVERROR_INVALIDDATA`
因此也被升级为致命失败。

复现文件：`D:\audioTest\陶喆 - 爱我还是他.flac`，44.1 kHz、双声道，时长 292.667 秒。
完整解码已经输出全部 12,906,600 帧，随后 FFmpeg 报告 `invalid sync code` /
`invalid frame header`，返回 `-1094995529`（`AVERROR_INVALIDDATA`）。

## 为什么表现为“下一首可以，seek 无效”

1. `Decode/PcmDecoder.cs`：旧代码遇到此错误返回 0；`636f4844` 改为抛出 `IOException`。
2. `Playback/Session.cs`：旧流程标记输入结束并等待下一次 seek；新异常路径记录
   `DecodeFailure`，退出解码线程，在 `finally` 中释放解码器。
3. `Playback/PlaybackEngine.cs`：新增的 PCM 失败检查调用 `StopAndNotifyLocked()`，
   暂停输出并发送 `PlayState(false)`，不发送 `PlayEnded`。
4. 主程序只在 `PlayEnded` 时执行自动切歌，因而停在当前曲目。seek 虽能重置环和位置，
   已退出的解码线程不会再处理请求；手动下一首重新创建 Session，所以仍可工作。

实际 DirectSound、淡入淡出开启的修复前测试停在约 291.9 秒。解码线程领先输出设备，
所以即使所有音频已经解码，失败停机仍会截断尚在环/设备中的尾音。

此前 `96cb8e82` 的自动切歌待处理请求修复针对“已经收到结束事件，但上一轮切歌仍在执行”的情况；
本例没有发出结束事件，因而该修复不能解决本例。

## 1.2.2.0 之后与播放相关的主要改动及目的

| 提交 | 改了什么、为什么改 | 与本例的关系 |
| --- | --- | --- |
| `16220dfe`、`c7f9444c`、`a4defa5d`、`4b1ec47d`、`ae65314b` | 卷积校正绑定稳定设备 ID；修复迟到试听、设备切换、启动发布及草稿恢复；分离 FIR 系数与渲染历史并缓存准备结果。 | 影响 DSP 和设备切换，未引入本例的曲尾失败分支。 |
| `ecac76a9`、`79544207` 等许可提交 | 增加购买/试用门控，限制 Atmos、5.1 和部分音效能力的使用入口。 | 普通本地双声道 FLAC 不依赖这些输出能力。 |
| `4ac4e6e9` | 播放状态和命令通知在 UI 线程发布；Play/Pause 在途时保留最新意图，退出后阻止迟到操作。 | 解决命令时序及线程问题，未改变解码 EOF。 |
| `e8c313fc`、`e4ace2e0`、`3ad8f2fc`、`055b7aa9` | 拆分启动、退出、进程、媒体控制和状态持久化；统一播放入口到 `PlaybackCoordinator`，跟踪任务并取消过时的曲目展示工作，统一进度显示状态。 | 改变主程序播放调度和生命周期；本例在引擎发通知之前就停止。 |
| `3db5dad7` 及后续外部文件修复 | 支持系统默认音频应用/外部文件激活；处理空队列和外部曲目进入播放列表的行为。 | 涉及播放入口及队列，非此文件解码失败的来源。 |
| `c50f1c16`、`2f79b2b0` | Atmos 和已识别的 5.1 自动协商独占输出，失败回退 PCM/共享输出；保留用户的输出偏好。 | 普通立体声 FLAC 仍走 PCM；本例无需触发自动独占。 |
| `b25d631a`、`636f4844` 的 FFmpeg DLL 更新 | 为 HTTP(S) / WebDAV 增加网络协议及代理支持，保持 FFmpeg 9.0.1 的音频编解码能力。 | 用同一套当前 DLL 分别运行旧/新解码代码也能复现差异，已定位到 C# 错误处理。 |
| **`636f4844`** | 网络源异步准备、限长管道、预缓冲、断流恢复、超时取消、Range seek；把真实读取失败与自然结束分开。换曲淡出增加 `_playGen` 检查，防止旧延迟任务覆盖后续会话。 | **共用 `PcmDecoder.Read` 从“错误返回 0”改为“抛异常”，Session 记录失败，watchdog 停机但不发结束事件，是本例的回归来源。** |
| `cf4469a9` | 主程序接入 WebDAV 播放和远程状态；远程会话使用带身份的结束状态，本地旧通知在远程播放期间被忽略，防止重复切歌。 | 当前为本地曲目；根因已在不启动 WinUI 的真实引擎测试中复现。 |
| `a614255d` | 大网络 DSF 缓冲改用有界原生内存、停止时释放；支持网络 DSD 位流；改进 DSF seek，并抽取 HTTP I/O 超时取消公共代码。 | 延续了上述错误处理，并非最初引入点。 |
| `96cb8e82` | 自动切歌执行期间保留一次新结束请求，避免短曲目结束被单飞守卫丢弃。 | 没有结束通知时无法起作用。 |

## 本次修复

- `PcmDecoder.Read` 对可恢复的 `AVERROR_INVALIDDATA` 继续排空解码器并读取到真实 EOF，
  保留有效 PCM 和 EOF 后的 seek 生命周期；连续无效帧恢复上限为 32 次，避免不推进的异常循环。
- 其他解码错误、网络 I/O 错误、取消和超时仍走原来的失败路径，未把所有异常恢复为 EOF。
- 正常解码只增加局部整数状态及错误分支，没有新增队列、任务、定时器或逐帧日志。
- 更新 `Player/AudioPlayer.exe`，供主程序打包和运行使用。
- 增加生成式短 FLAC 曲尾损坏样本及解码/结束/seek 回归；测试样本不包含用户音乐。
- 改动范围为 PCM 解码器、播放测试、播放器产物和本报告/变更记录。

## 验证证据

- 同一套当前 FFmpeg DLL：1.2.2.0 解码代码、修复前代码和修复候选都输出完整
  12,906,600 帧；旧版和修复版进入 EOF，修复前代码抛异常。
- 用户文件的完整解码、稳定 EOF、中点 seek、回到起点，以及 DirectSound 模式的
  Session 排空和 EOF 后 seek（淡入淡出开/关）全部通过，共 11 项。
- 原生 DirectSound 曲尾测试（跳到末尾 4 秒，静音、开启淡入淡出）：修复前缺失 `PlayEnded`；
  修复后只收到一次结束通知，EOF 后 seek/恢复推进，后续曲目能启动。
- 随后用相同配置从头完整播放用户文件 292.667 秒，同样只收到一次 `PlayEnded`，
  EOF 后 seek/恢复和下一曲启动均通过；未依赖跳到曲尾来完成这一轮验证。
- 播放回归 295/295 通过；NativeAOT 发布通过。
- 真实 NativeAOT 网络回归 50 项通过，包括截断传输、401、超时、取消、重试、Range seek、
  不可信 TLS 证书拒绝及大 DSF 会话释放。TLS 测试需在沙箱外创建临时测试证书。
- 网络回归未启用 `--play`；本次设备播放验证使用 DirectSound。尚未在完整 WinUI 界面
  手动验证列表自动前进，也未对其他输出设备进行实机测试。

复现/验证命令（在仓库根目录执行，原生测试目录中需包含 FFmpeg DLL）：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression -c Release -- --test-pcm-file "D:\audioTest\陶喆 - 爱我还是他.flac"
dotnet run --project _tools/PlaybackSwitchRegression -c Release
dotnet run --project _tools/AudioPlayerSmokeTest -c Release -- artifacts/flac-end-fixed/AudioPlayer.exe "D:\audioTest\陶喆 - 爱我还是他.flac" 310 --mode=DirectSound --vol=0 --fade --expect-end --next=_tools/test_tone.wav
dotnet run --project _tools/StreamingRegression -c Release -- artifacts/flac-end-fixed/AudioPlayer.exe
```

本次详细日志保存在被 Git 忽略的 `artifacts/flac-end-diagnosis/` 中。
