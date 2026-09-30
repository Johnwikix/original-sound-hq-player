# 歌词原生渲染生命周期回归

构建后运行输出目录的 `LyricsRenderRegression.exe`，结果写入同目录 `result.txt`。测试使用屏幕外的隔离 WinUI 窗口，不访问音乐库、音频引擎或网络。

```powershell
dotnet build _tools/LyricsRenderRegression
```

运行生产 `LyricsRenderCoordinator` 和真实 `CanvasAnimatedControl`。第一帧生成 Win2D 缓存后，暂时保持绘制回调未返回；UI 线程连续发布两份歌词，检查活动帧的 CommandList／文字布局仍然存活。释放帧后，检查最新歌词生效、旧资源释放。随后覆盖 300 次快速发布、暂停时重绘、空歌词、带待发布内容的重复关闭。

`MotionChecks` 在真实游戏循环线程运行生产协调器和动画器，控制播放时钟逐帧推进，检查可见区顶部传播、播放行位移与缩放／模糊／透明度共同等待、短句削弱与关闭错峰、seek 清除延迟和布局重建，并用真实 Win2D 渲染目标绘制更新后的效果。

`HighlightChecks` 复现《Unchained》83.675–96.652 秒的空档，检查高光保持、入句边界同步切换、向前／向后跳入空档、暂停、末行和重叠；真实 Win2D 像素校验确认空档仍绘制已播放填充，逐字进度已完成且没有延长。

`DesktopHighlightChecks` 编译生产桌面歌词渲染器，在独立屏幕外 WinUI 窗口通过实际 SetLyrics／SetPlaybackTime 和 Canvas 更新／绘制回调验证相同空档、切句和回拖；两种宿主都消费同一个展示高光边界。

`PlaybackTimingChecks` 使用《The Story of Us》完整旁挂文件，经生产解析器及 `LyricsRefreshService` 生成播放快照，在真实 Win2D 游戏循环线程逐帧绘制 153255–157681ms。检查 brave 的已播填充、重音缩放／发光确实启动并在切句前回落，字浮归位，以及 157681ms 精确切句和向后拖动。`PlaybackTimingAdapters` 仅替代本场景不会调用的数据库／网络／一次性缓存边界，若意外调用直接失败；不替代计时、动画、原生布局或绘制。

修复前可稳定检测到 UI 在帧结束前释放原生缓存；这锁定了崩溃调用链的资源生命周期错误，并不保证每次都触发驱动／运行时的 AccessViolation。反射用于观察生产协调器中的行与资源、调用生产帧更新入口，不替换渲染、线程调度或释放方法。此测试不代替用户设备上的完整播放切歌验收。
