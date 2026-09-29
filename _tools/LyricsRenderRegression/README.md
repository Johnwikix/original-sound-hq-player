# 歌词原生渲染生命周期回归

构建后运行输出目录的 `LyricsRenderRegression.exe`，结果写入同目录 `result.txt`。测试使用屏幕外的隔离 WinUI 窗口，不访问音乐库、音频引擎或网络。

```powershell
dotnet build _tools/LyricsRenderRegression
```

运行生产 `LyricsRenderCoordinator` 和真实 `CanvasAnimatedControl`。第一帧生成 Win2D 缓存后，暂时保持绘制回调未返回；UI 线程连续发布两份歌词，检查活动帧的 CommandList／文字布局仍然存活。释放帧后，检查最新歌词生效、旧资源释放。随后覆盖 300 次快速发布、暂停时重绘、空歌词、带待发布内容的重复关闭。

修复前可稳定检测到 UI 在帧结束前释放原生缓存；这锁定了崩溃调用链的资源生命周期错误，并不保证每次都触发驱动／运行时的 AccessViolation。反射只用于观察生产协调器中的行与资源，不替换渲染、线程调度或释放方法。此测试不代替用户设备上的完整播放切歌验收。
