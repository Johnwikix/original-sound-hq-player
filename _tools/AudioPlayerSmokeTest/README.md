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
