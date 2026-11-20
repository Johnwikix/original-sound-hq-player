# 首次播放计时

从仓库根目录执行。使用发布的 NativeAOT EXE、真实命名管道及默认 WASAPI 共享设备，音量为 0.02。每次测试启动独立进程，退出时关闭管道并等待播放器结束。

先构建工具，将播放器和随应用分发的原生库放入隔离目录：

```powershell
dotnet build _tools/AudioPlayerSmokeTest -c Release --no-restore
New-Item -ItemType Directory -Path artifacts/audio-startup-fixed -Force | Out-Null
Copy-Item Player/AudioPlayer.exe, Player/wavpackdll.dll artifacts/audio-startup-fixed
Get-ChildItem Libraries/FFmpeg/x64 -Filter *.dll | Copy-Item -Destination artifacts/audio-startup-fixed
```

分别验证立即发送 Play、首次播放按钮、连接后空闲再点击播放：

```powershell
_tools/AudioPlayerSmokeTest/bin/Release/net11.0/AudioPlayerSmokeTest.exe artifacts/audio-startup-fixed/AudioPlayer.exe _tools/test_tone.wav --startup-timing
_tools/AudioPlayerSmokeTest/bin/Release/net11.0/AudioPlayerSmokeTest.exe artifacts/audio-startup-fixed/AudioPlayer.exe _tools/test_tone.wav --startup-timing --play-button
_tools/AudioPlayerSmokeTest/bin/Release/net11.0/AudioPlayerSmokeTest.exe artifacts/audio-startup-fixed/AudioPlayer.exe _tools/test_tone.wav --startup-timing --play-button --idle-ms=2000
```

- `connected`：进程启动到双管道握手和初始 DSP 状态到达。
- `confirmed`：播放请求到真实执行确认。
- `advancing`：播放请求到 Playing 状态及正向播放进度，包含设备缓冲和 50ms 状态推送间隔；不等同于测量扬声器出声。
- `--max-play-ms=1000`：进度推进时限，默认 1000ms，超限或未播放时返回非零退出码。

新进程不代表系统文件缓存已清空，不能将该计时直接作为重启电脑后的磁盘冷启动数据。

慢速原生库加载、加载失败、加载中退出的确定性回归：

```powershell
dotnet run --project _tools/PlaybackSwitchRegression -c Release --no-restore -- --test-startup
```

这些测试使用真实播放器服务和 IPC，在子进程的原生库加载边界阻塞/释放，验证解码依赖就绪前不会完成握手；输出设备行为另由上述发布 EXE 测试覆盖。

## 实际设备 DSP 与内存边界

`<AudioPlayer.exe> <合成WAV> 60 --hardware-dsp --mode=WasapiShared` 验证源率、固定 96 kHz、共享峰值预算改变 3 dB、整数倍 176.4 kHz 与重复切歌，并在 artifacts 保存进程内存/句柄和实际输出模式。`WasapiExclusiveEvent` 与 `ASIO` 分别探测对应设备；明确记录共享兼容回退，不将回退算作目标模式通过。使用低电平夹具，播放器会话音量为 0.02，不更改系统混音格式。`--mode=ListAsio` 仅列出可用 ASIO 驱动。

`<AudioPlayer.exe> <六秒WAV> 6 --gapless --rate=1.5 --vol=0.02 --ir=<WAV-IR>` 额外启用真实卷积和 True Peak，验证同一输出上的无缝接管、暂停/恢复与曲尾排空。IR 路径按调用工作目录解析为绝对路径。以上检查不代表 DAC 模拟输出测量或听感验收。

测试曲间过渡时，使用两个同采样率、同声道布局的六秒 PCM 文件。下面的选项沿用同一冒烟入口，检查发布版播放器通过实际输出设备交接后的进度、暂停恢复和结束次数。探针的交叉淡化时长为两秒。

```powershell
$probe = '_tools/AudioPlayerSmokeTest/bin/Release/net11.0/AudioPlayerSmokeTest.exe'
& $probe $player $first 6 --crossfade --rate=1 --vol=0.02 "--next=$second"
& $probe $player $first 6 --smart-transition --rate=1 --vol=0.02 "--next=$second"
& $probe $player $first 6 --smart-transition --album-boundary --rate=1 --vol=0.02 "--next=$second"
```

验证曲中按「下一首」时，将第一首换成 30 秒的文件，并添加 `--transition-now`。探针会在距曲尾至少 15 秒时请求过渡，检查交接时间、下一首已混入的进度及最终结束通知。添加 `--queue-first` 可复测已提交下一首计划的路径。

## 独占设备的异采样率切歌

准备两个时长不同且至少 20 秒的静音双声道 PCM WAV，使用 44.1 kHz 与 48 kHz，或设备均支持的另一组采样率。传入同一个发布目录下的 NativeAOT 播放器，保留随包分发的原生库。先列出设备，再用名称中的唯一片段选定一个实际端点或驱动。

```powershell
$probe = '_tools/AudioPlayerSmokeTest/bin/Release/net11.0/AudioPlayerSmokeTest.exe'
$player = 'path/to/publish/AudioPlayer.exe'
$first = 'path/to/silent-44100.wav'
$second = 'path/to/silent-48000.wav'
& $probe $player $first --list-output-devices
& $probe $player $first --mode=WasapiExclusiveEvent '--device-name=扬声器 (Senary Audio)' --rate-mismatch-fallback=manual --expect-rate=44100 --next-rate=48000 "--next=$second"
```

对实际设备重复 `--rate-mismatch-fallback=natural`，以及 `WasapiExclusivePush` 和 `ASIO`，再交换文件和采样率参数测反向。自然用例会先定位到当前曲尾前三秒，再观察真实曲终。手动用例会提交候选曲并查询 `TransitionNow`，只接受 `Preparing` 后转为 `Unavailable`。两种用例都检查失败的候选未换曲，一次普通 `Play` 后重新以指定设备和目标采样率推进，并统计曲终和自动交接通知。实际退回共享或驱动未启动时返回失败。所有用例的播放器音量为 0。

自然用例加 `--no-queued-candidate` 可直接比较同一个设备和采样率组合下的曲终、普通换曲，不提交候选曲。

若已注册的 ASIO 驱动无法启动而播放器退回共享，`--shared-fallback-rate=<系统混音率>` 可单独验证该路径。结果会明确记录实际共享模式，ASIO 设备验证仍以原生输出为准。
