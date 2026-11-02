# 歌曲详情声谱图回归

先发布共用 worker，再运行原生解码、协议、缓存和取消检查。进程退出后的独占句柄检查应在正常 Windows 权限环境运行；受限沙箱中曾观察到子进程已退出后仍有短暂共享冲突：

```powershell
dotnet publish External/AudioAnalysisWorker/AudioAnalysisWorker.csproj -c Release -r win-x64 --no-restore -o Player
dotnet run --project _tools/SpectrogramRegression/SpectrogramRegression.csproj -c Release
```

真实 WinUI 绘制和页签生命周期检查：

```powershell
dotnet build _tools/HomeUiRegression/HomeUiRegression.csproj -c Debug -p:Platform=x64
& _tools/HomeUiRegression/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/HomeUiRegression.exe --spectrogram
```

结果与浅/深色截图位于 UI 回归输出目录。数值回归覆盖 Hann 窗幅度归一化、反相立体声、短静音、原采样率高频、完整远程缓存租约、文件版本失效、实际 worker 退出，以及原 MIR 协议兼容。UI 回归使用真实控件和 Dispatcher，替换分析输入以检查绑定、播放状态、重试、取消和迟到结果。

MP3 尾标签兼容使用 `Fixtures/Tone.mp3`（由 FFmpeg 生成的 0.4 秒、1 kHz 双声道正弦音）附加合成 Lyrics3 标签，正常回归会执行；也可仅检查兼容性并附带一个本地样本：

```powershell
dotnet run --project _tools/SpectrogramRegression/SpectrogramRegression.csproj -c Release -- --mp3-compatibility 'C:\Music\sample.mp3'
```

列表标题入口的内存集成检查使用真实 `MusicDetailsPresenter`、控件、缓存服务和 worker。元数据与封面宿主由测试桩提供：

```powershell
$env:SPECTRUM_MEMORY_SONG = 'C:\Music\sample.mp3'
& _tools/HomeUiRegression/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/HomeUiRegression.exe --spectrogram-memory
```

`spectrum-memory.json` 记录六次标签页、十二次频谱进出后的弱引用存活数、托管/私有内存、进程显存和缓存字节数；同时验证已卸载位图不能再读取。强制 GC 与最终设备缓存 Trim 仅存在于探针，活对象断言使用 Trim 前的样本；生产代码按卸载时机释放资源，不强制 GC、不关闭共享设备。

声谱图是播放器概览：默认 FFT 2048、50% 重叠、最多 1024 个时间列，按列平均功率；发布的强度矩阵以 1 dB 精度覆盖 -160～0 dBFS。多声道音频由 FFmpeg 下混成立体声后分析，左右声道在频域合并功率。未覆盖真实音频设备输出、跨显示器 DPI 或系统高对比度切换。
