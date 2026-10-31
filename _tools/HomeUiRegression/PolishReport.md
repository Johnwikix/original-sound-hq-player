# 首页交互与第二批生命周期优化验证报告

日期：2026-10-31。范围：保留 Frame 与官方动画，执行悬浮定位按钮、首页封面/预览修复和已确认的第二批清理；未修改 CsWinRT 或播放器协议。

## 结论

本次有可验证的内存收益。首页预览及轮播定时器以前只 Stop，没有解绑 Tick。后台 WinUI 夹具逐次创建、加载、卸载 31 个实际 HomePage，停止后的原生 Tick 回调仍可保留页面。卸载解绑、重载重新订阅后，无 UIA 观察者的进程中 31 页全部回收。仅在测试夹具里恢复一个 Tick 订阅，就重新出现 31 页全部保留。

这证明了本项目这一处应用层保留问题及修复效果，不能据此判定 CsWinRT #1213 已解决，也不能把其他页面的 Frame 缓存算作泄漏。其他页面缓存策略沿用第一批决策，HomePage 继续显式 Disabled；生产代码没有添加 GC.Collect。

## 已实现行为

| 位置 | 最终行为与资源责任 |
| --- | --- |
| 歌曲、收藏、分组详情、歌单详情、两个播放队列 | 同一自定义圆形按钮覆盖在 ListView 右下角，44×44、边距 16；复用现有命令、提示和无障碍名称。点击定位，列表正常滚动不受影响。 |
| MusicCoverControl / StoreCarouselImageMotion | 缺图占位与图片同处动画层，缺图及其下一张图片均进入动画；Light/Dark 使用不透明主题底色。统一 8px 原生合成裁切；完成、替换、停止及卸载释放上一张源和 scoped batch。 |
| HomeSongPreview / HomePage | 删除三个推荐栏共用悬浮面板中的第四行推荐描述。进入属性页保留预览源至连接动画完成/取消，阻止期间关闭定时器抢先收起。辅助技术 Invoke 与点击复用同一入口。 |
| HomePage / FeaturedCarousel | Tick 按 Loaded/Unloaded 成对订阅和解绑，卸载停止调度，重载仍能工作。 |
| AlbumPage / ArtistPage / FolderBrowsePage / PlayListPage | ConnectedAnimationScope 拥有快照与完成订阅；替换、离页、外层缓存页卸载均取消。排队回调核对代次，失效目标安全完成，返回动画清理关闭状态。 |
| MusicDetailsWindow / AlbumDetailWindow | WindowArtworkSession 拥有原始字节、解码图片与取消源。关闭解除发布回调、移除图片绑定并停止跟踪；迟到读取不得发布；在线空结果保留已有封面，取消回调失败仍继续清理。已接受的文件写入与封面导出使用操作开始时的快照。BitmapImage 不支持 IDisposable，清理使用断开引用。 |
| Stats / 添加文件夹 | SQLite 每条真实只读查询前后检查取消，不把“停止等待”当成 SQL 已完成。跳过后续过期查询；多个刷新合并为在途任务与一次最新读取。扫描和已接受写入继续完成，退出等待实际工作。 |
| AboutSettingsControl | 每次加载恢复悬停事件，卸载解绑、停止滚动并关闭说明对话框。DSP 卸载提交逻辑保留。 |

## 实际验证

| 检查 | 结果与边界 |
| --- | --- |
| 主项目 x64 Debug | 成功，0 错误、379 个编译/工具链警告；见 [构建日志](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/main-build-final.log)。未执行 NativeAOT 发布。 |
| 完整首页/属性编辑器原生回归 | 184 项通过；实际 HomePage、控件、编辑器与 SQLite 边界。[结果](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/full-home-regression.txt)。 |
| 新增首页与资源生命周期原生回归 | 53 项通过，12 次 lvt UIA Invoke。覆盖缺图→正常封面的实际 Composition 控制器、明暗实心色、圆角裁切、删除描述、保留预览至真实 ConnectedAnimation.Completed、窗口封面迟到/关闭/新请求覆盖、动画取消/重载/失效目标，以及页面释放。[结果与 UIA 日志](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/final-ui/result.json)。 |
| 悬浮定位原生回归 | 103 项通过，12 次 lvt Invoke；包括点击实际歌曲/收藏按钮。44px 右下角覆盖布局、变高虚拟行、重复队列条目、快速重入、取消和边界检查通过，非首尾样本居中偏差 0 DIP；缓存返回以可见行锚点验证，偏差 0 DIP。[结果](G:/SoftwareProject/winui/music_player/_tools/ManualLocationUiRegression/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/artifacts/result.json)。 |
| 共享状态与 StatsViewModel | 通过；取消实际查询前仍等待、最新范围、离页、UI 通知与既有状态回归。[日志](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/shared-state.log)。 |
| 实际 SQLite 与扫描兼容 | 通过；阻塞真实 SQLite 查询期间连续 20 次源变更合并，Stop 等待 SQL 完成，取消跳过后续计数/Top 查询，连接可复用；既有扫描、提交、元数据、FFmpeg 回归也通过。[日志](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/folder-sqlite.log)。 |
| 前台隔离 | lvt 与内存夹具均为独立进程，屏幕外 Show(false)、WS_EX_NOACTIVATE、不进入窗口切换列表。每 10ms 检查，前台违规 0；只使用 dump/invoke，没有真实鼠标、键盘或激活操作。未附加或启动当前用户生产程序。 |
| 静态检查 | 编辑文件 CRLF、XAML/XML、git diff --check 通过；改动行 GetString 的独立键在七语言均存在。[结果](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/static.json)。 |

已查看 PrintWindow 捕获的真实合成截图：[定位按钮](G:/SoftwareProject/winui/music_player/_tools/ManualLocationUiRegression/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/artifacts/songs-centered.png)、[首页圆角与占位](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/final-ui/home-polish-carousel.png)、[Dark 占位](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/final-ui/home-polish-placeholder-Dark.png)、[预览](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/final-ui/home-polish-preview.png)。Dark 图只切换被测页面主题，夹具背景固定浅色，不代表主程序 Dark 背景。

## GC 与内存对照

同一生产代码和测试二进制，单独启动两个无 UIA 观察者、无 trace 的进程，顺序执行相同场景：40 首夹具歌曲、每轮 10 次 HomePage 创建/加载/卸载，共 31 页；每轮结束用诊断 GC 检查可达性，并等待终结器、让 UI 消息循环继续运行。诊断 GC 只存在于测试中。内存来自最后一轮诊断后的当前进程计数，MiB=1,048,576 字节。

| 指标 | 测试中仅恢复一个已停止 Tick 订阅 | 正常卸载解绑 |
| --- | ---: | ---: |
| 已创建 / 已卸载首页 | 31 / 31 | 31 / 31 |
| 完整 GC 后仍存活首页 | 31 | 0 |
| CWT 终结哨兵确认回收 | 0 | 31 |
| 托管内存 | 25.80 MiB | 11.95 MiB |
| 进程 Private Bytes | 308.84 MiB | 182.95 MiB |
| 进程句柄 | 1415 | 1198 |
| DXGI 适配器索引 0 Local CurrentUsage | 99.60 MiB | 75.47 MiB |
| DXGI 适配器索引 0 NonLocal CurrentUsage | 14.47 MiB | 14.36 MiB |

原始记录：[单订阅对照](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/dxgi-negative-control/state.json)、[最终正常组](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/dxgi-memory-final/state.json)。两组热身 Private Bytes 均约 160 MiB，第一轮到第三轮保留组持续增长，正常组全部卸载页面在诊断后可回收。较早未添加 DXGI 的独立对照也同样复现 31→0；不同轮次私有内存值有波动，不能承诺每个曲库节省固定大小或比例。

UIA 专门检查过的首个页面在 lvt 夹具进程中会保留，后续 30 页全部可回收；原始 AlivePageIndices 确认为索引 0。故页面回收结论采用完全独立、无 UIA 的两组进程，未把自动化观察的影响混入对照。

PDH GPU Process Memory 的 Dedicated Usage 在本机出现与 DXGI CurrentUsage 不一致的累加；原始数据完整保留。微软记录过[专用显存计数误报](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/gpu-process-memory-counters-report-wrong-value)，但这本身不能证明本机就是同一系统缺陷。交叉采用 [IDXGIAdapter3::QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo) 的当前进程读数，逐适配器记录，表中仅列索引 0，没有直接累加多个枚举实例。正常组 Local Usage 也曾短暂到约 118 MiB，因此这里只能说明本场景末尾较低，不能认定所有资源立即归零或存在稳定显存降幅。

## GC 停顿证据

额外单独启动进程采集 EventPipe，排除在上述内存对照之外。用 SuspendEEStart→RestartEEStop 计算包括线程汇合及恢复的暂停窗口，不将整个后台 GC 的 Start→End 当作 STW。

| 阶段 | 暂停窗口数 | 累计暂停 | 最大单次 | P95 |
| --- | ---: | ---: | ---: | ---: |
| 自然创建/卸载压力阶段 | 117 | 191.292 ms | 7.026 ms | 3.608 ms |
| 测试主动诊断 GC 阶段 | 9 | 28.639 ms | 5.398 ms | 5.398 ms |

[阶段汇总](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/gc-trace/phases.json)、[完整分析](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/gc-trace/summary.json)、[原始 trace](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/gc-trace/gc.nettrace)。自然阶段没有测试代码显式 GC，但运行时仍会报告 InducedNotForced 等原因；不能把它们都解释为分配触发的 GC。该数据没有修改前的同条件 trace 对照，只用于确认实际暂停范围，不声称改善比例、零分配或已证明没有掉帧。

## 验证范围与复跑

宿主使用 Windows App SDK 2.5.1、项目同版自编译 CsWinRT 2.3.2-prerelease.261026.1-nogcpressure、.NET 11 x64。独立 WinUI 宿主使用实际页面/控件/编辑器实现和真实原生渲染；业务适配器隔离播放器、用户文件和远程服务。窗口封面资源会话使用真实 BitmapImage 解码，但没有在完整生产应用里逐一打开旧属性窗口、文件选择器、About 说明对话框与两个队列 TeachingTip 做设备交互验证。四类页面使用统一动画所有权帮助类，其中真实原生动画取消/重载由夹具验证；完整生产导航序列未逐个录屏。High Contrast 资源已提供，但未切换系统高对比度；未测 Release/AOT、长时间运行、真实大曲库和主程序 Satori GC 发布环境。因此，测试结果不等同于所有设备上的主程序性能保证。

```powershell
# 从仓库根目录运行；测试窗口只后台显示，不操作前台。
./_tools/HomeUiRegression/Run-Polish.ps1
./_tools/HomeUiRegression/Run-Memory.ps1 -RunName replay-control -RetainTick
./_tools/HomeUiRegression/Run-Memory.ps1 -RunName replay-normal
./_tools/HomeUiRegression/Run-Memory.ps1 -RunName replay-trace -Trace
./_tools/ManualLocationUiRegression/Run.ps1
```

Run-Polish 构建后使用当前 SDK/包缓存。Run-Memory 复用该二进制；RetainTick 只影响夹具，不修改生产文件。原始 artifacts 不参与产品发布。功能记录已统一追加在 docs/Changelog.md 顶部。
