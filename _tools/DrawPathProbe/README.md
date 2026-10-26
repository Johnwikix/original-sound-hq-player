# Draw 分配与原生 ABI 回归

在仓库根目录运行 `pwsh -File _tools/DrawPathProbe/Run.ps1`。默认使用隔离的屏幕外 WinUI 窗口、真实 CanvasAnimatedControl、D3D/DirectWrite 资源和生产渲染器，不访问音乐库、音频引擎或用户数据库。无需 Computer Use。

`-Trace` 调用已安装的 dotnet-trace，并用 EventSource 标记各测量阶段；`TraceAnalysis` 使用 TraceEvent 的 EventPipe 读取路径转换并分析分配栈。示例：

```powershell
pwsh -File _tools/DrawPathProbe/Run.ps1 -Trace
dotnet run --project _tools/DrawPathProbe/TraceAnalysis -c Release -- <input.nettrace> <output.json>
```

精确字节数来自预热后 `GC.GetAllocatedBytesForCurrentThread`。分配栈中的 AllocationTick 是采样区间，按类型和栈归因的字节数为近似值；采样线程时间包含等待，不能当成 CPU 占用或 GPU 耗时。提交时间包含驱动背压且不等待 GPU 完成。采集过程不主动触发 GC。

## 场景与已验证结果

2026-10-26，Windows x64、.NET 11 RC2、CsWinRT `2.3.2-prerelease.261026.1-nogcpressure`、Win2D 1.4.0、96 DPI、1184×761 DIP。该宿主使用标准运行时，不能将它等同于完整播放器的 SatoriGC 发布进程。歌词为 40 行合成数据，每行 6 个词，附翻译和发音。

| 操作 | 改造前 | 改造后 |
|---|---:|---:|
| 稳定歌词真实回调，Draw 边界 + DrawingSession + layer | 912 B/帧 | 0 B/帧 |
| 同场景 Update | 40 B/帧 | 0 B/帧 |
| 原始指针位置/滚轮 getter 重放 | 约 400 B / 1.2 KB | 0 B/次 |
| 旋转网格背景（缓存预热后） | 1536 B/次 | 96 B/次 |
| 复用缓存命令列表的 DrawImage | 0 B/次 | 0 B/次 |
| 整份歌词重发、布局和首轮绘制 | 约 1.94 MB/次 | 约 1.23 MB/次 |

稳定歌词场景固定播放位置并暂停播放时钟，配置了效果开关；它不能代表整首歌逐字播放始终零分配。移动换行、首次效果创建和布局重建仍会分配。采样栈确认主要稳定分配来自会话和 layer RCW，Update 的 40 B 来自 IList 枚举器；冷重建仍包含字符动画状态等必要分配。

原始 ABI 测试将矩阵、颜色、源/目标矩形、透明度、裁剪和画刷 layer 的真实 GPU 像素与原 Win2D 投影逐字节比较，覆盖 96/144 DPI。字符效果测试验证按需创建、复用、重复释放和重建后的像素一致性。`FrameAbiRegression` 验证真实 Draw、线程、在途回调屏障、自行/重复解绑和迟到回调；完整 `LyricsRenderRegression` 验证生产主歌词与桌面歌词行为。

基线、候选 JSON、nettrace 和栈分析输出保存在仓库忽略的 `artifacts/`，不会编入主程序。自然 GC/Gen2/STW、完整播放器长时间播放、ARM64 以及不同驱动设备未在本次测量中验证。

## 指针与扫描

`-InputCapture` 使用 C# Win32 测试驱动：先尝试向本进程离屏窗口排队输入消息，若不能形成 WinUI 路由事件，再在显式前台测试窗口使用 SendInput。它校验实际送达数量、前台进程和 Escape 取消，结束后在鼠标未被用户移走的条件下恢复位置。需要可交互 Windows 桌面；不会把未送达的消息计为成功。取消重建窗口内容并显式恢复激活后，自动驱动已送达真实移动和正负滚轮事件，逐事件核对原始 ABI 与投影值。没有使用 Computer Use。

`PointerPointScope` 在同一 UI 回调中读取 Position/MouseWheelDelta 并释放全部原生引用；协调器保留 Handled、悬停、滚轮过渡及 MVVM 入口。已验证 getter 重放从约 400 B（位置）/约 1.2 KB（原双点加滚轮）降至 0 B；生产移动回调体从约 300–346 B/事件降至 0 B。滚轮初次接入剩余 24 B 来自 ValueTransition 的 object.Equals 装箱，改用泛型相等比较保持原有守卫和延迟语义。最终一轮自动采集在预热后取得 75 次移动、15 次正负滚轮，逐事件对照值一致，两个生产回调体均为 0 B。计数不包含 WinUI 在进入处理器前创建事件参数的开销，也不代表硬件输入率。

扫描使用现有 `FolderScanRegression` 的真实临时 SQLite、FFmpeg 文件/句柄与取消、背压回归。10 万条合成调度测量中，已有有界管线约 590 KB，对照旧式每文件任务约 9.20 MB；这排除实际标签读取、数据库和 UI 成本，也不是本次新增收益。当前没有足够证据支持把扫描改成 ABI 链路。

## 有界生命周期压力测量

构建后直接运行 `DrawPathProbe.exe --lifetime --seconds=300`。使用生产协调器、真实游戏循环、原生中间会话与 layer，每三秒重建歌词；结果逐十秒覆盖写入 `result.json`。自然 GC 样本与最后清理资源后的显式回收样本分开记录。工作集和 Private Bytes 包含托管与原生部分，不能当作原生堆或显存分项；GPU 进程计数器需按结果 PID 单独采样。此宿主使用标准运行时，不等同于完整播放器的 SatoriGC/用户设备长时播放。
