# 功能变更记录

新条目加在最上方。

## 2026-09-18 全局进度指示：进度环统一收归 MainPage 标题栏，支持多操作并发显示

- `ViewModel/ProgressCenter.cs`：新增全局进行中任务中心（`Begin`/`Report`/`Complete` 按 Key 管理条目，非 UI 线程调用自动转派 DispatcherQueue）；多操作横排并列、各自显示百分比，**不做跨操作加权平均**（新操作加入会使平均值倒退，观感等同进度回退），仅"恰好一个操作且其上报百分比"时进度环用确定进度并显示独立百分比元素，否则不定进度
- `View/MainPage.xaml`：标题栏 AppTitle 旁的任务指示器改为绑定 ProgressCenter——环 + 百分比 + 操作文本横排，承接原 MusicBrowsePage 顶栏环的全部功能（任何页面与播放详情页均可见）
- `View/MusicBrowsePage/MusicBrowsePage.xaml`：移除顶栏传输进度环、百分比文本与常隐的"正在传输"TextBlock
- `ViewModel/AppViewModel.cs`：移除 `ProcessRingVisibility`/`ProcessRingPercent(Text)` 与标题栏任务派生属性（`IsTitleBarTask*`/`TitleBarTaskText`/`LibraryScanPercent*`/`IsLibraryScanning`/`IsLibraryLoading`/`IsIpcConnecting`），新增 `Progress` 中心；`TransmitFileToUsb` 改注册 `UsbTransmitting` 条目
- `Services/StartupCoordinator.cs`：IPC 连接、库加载、启动扫描注册到 ProgressCenter（扫描百分比经 `Report` 上报，单调保护移入中心）
- `Services/LibraryWatcherService.cs`：文件监视重扫注册 `LibraryRescanning` 条目；不再手工 `TryEnqueue` 切换可见性（修复：重扫期间百分比残留上一轮传输值、并发操作互相隐藏进度环）
- `ViewModel/Pages/MusicBrowseViewModel.cs`：移除 `ShowTransmission`/`HideTransmission`
- `Strings/*/Resources.resw` ×6：新增 `ProgressConnecting`/`ProgressLoadingLibrary`/`ProgressScanning`/`ProgressRescanning`/`ProgressTransmitting`；删除 `TitleBarTask*` 与 `Transmitting.Text`
- `_tools/LifecycleRegression/Stubs.cs`：桩同步（`ProgressCenter`/`ToolUtils.GetString` 空桩，移除 `ProcessRingVisibility`）

## 2026-09-18 启动扫描无变更不再刷新页面

- `Services/InitialFileScan.cs`：`InitialScan`/`Deduplication` 改返回 `bool`（本轮是否产生数据库变更）；目录扫描中途失败按有变更保守处理
- `Services/StartupCoordinator.cs`：`ScanLibraryAsync` 仅在扫描有变更（或失败可能已提交批次）时调用 `RefreshSongsSourceAsync`——数据未变时列表不再被 Reset 二次重置，消除启动"闪两次"；完成日志附带变更结果
- `_tools/FolderScanRegression/RegressionSuite.cs`：补断言——重试扫描（有提交）返回 true、随后无变更扫描返回 false
- 兼容：`ScanChangedFolderAsync` 既有返回值（新增/更新/删除计数）透传，无数据库结构变化

## 2026-09-18 移除启动 LoadingGrid：主界面即刻显示

- `MainWindow.xaml(.cs)`：删除 LoadingGrid 过渡层；`ShowMainPage()` 只注入 MainPage
- `Services/StartupCoordinator.cs`：`ShowMainPage()` 提前到窗口内容加载后（用户协议之前，首装弹窗覆盖在主界面之上）；新增 `IsLibraryLoading` 指示（库任务创建前置 true、主轨 WhenAll 后置 false）；版本更新弹窗从 `StartAsync` 末尾移至 `EnableInteraction` 后台弹出（不再阻塞交互启用，关闭后才记录已读版本）
- `ViewModel/AppViewModel.cs`：新增 `IsLibraryLoading`，标题栏任务指示器扩为三态（连接音频引擎/扫描音乐库/加载音乐库）；`NotifySongsSourceChanged` 在未 Ready 时改刷当前页（`RefreshDataSource`）——主界面先行显示后缓存库到达需补刷视图，成本与首次导航相同
- `Strings/*/Resources.resw` ×6：新增 `TitleBarTaskLoadingLibrary`；删除仅 LoadingGrid 使用的 `LoadingText.Text`
- 行为变更：启动无全屏 Loading 过渡，列表数据到达后流入（空库占位防闪逻辑不变）；版本弹窗与后台扫描指示器可能同时出现

## 2026-09-18 启动提速：IPC 连接前置、主界面不再等待播放引擎

- `Services/StartupCoordinator.cs`：AudioPlayer.exe 拉起与 IPC 连移到 `StartAsync` 最前（用户协议之前），与数据库初始化、协议阅读并行；主轨 `Task.WhenAll` 只等许可 + 缓存库，LoadingGrid 不再等 IPC；新增引擎并行轨（等 IPC+许可+缓存库后 `InitializeMusic` 首推），`RetryStartupCorrectionsAsync` 移至引擎轨完成后触发
- `Services/IpcService.cs`：新增 `IsConnected`；重试循环检测退出（Dispose）时静默返回，协议被拒绝不再触发 20 秒失败路径
- `ViewModel/AppViewModel.cs`：新增 `IsPlaybackEngineReady`（Ready + IPC 连接 + 首推完成，由 StartupCoordinator 统一刷新）、`IsIpcConnecting`、`IsTitleBarTaskActive/Text/Indeterminate`
- `Services/PlaybackCommands.cs`：`CanPlay` 改判 `IsPlaybackEngineReady`，并订阅其变化刷新全部命令可用性（SMTC/任务栏随 CanExecuteChanged 联动置灰）
- `ViewModel/Pages/MusicBrowseViewModel.cs`：`PlayMusic` 入口在引擎未就绪时直接返回（双击列表等无按钮入口）
- `Utils/BindUtils.cs`、`View/MainPage.xaml`、`View/PlayingDetailPage.xaml`：新增 `IsPlaybackEntryEnabled`/`IsSwitchEntryEnabled`，主界面与播放详情页播放控制按钮在引擎就绪前置灰；AppTitleBar ProgressRing 改为通用任务指示器，显示当前任务（连接音频引擎/扫描音乐库）+ 扫描百分比
- `Strings/*/Resources.resw` ×6：新增独立键 `TitleBarTaskConnecting`、`TitleBarTaskScanning`
- 行为变更：协议确认前即拉起 AudioPlayer.exe（拒绝协议或启动失败时由 ShutdownCoordinator 清理）；Ready 不再隐含 IPC 已连接，窗口可先显示、按钮置灰至引擎就绪

## 2026-09-18 Win2D 封面移除、动画文本块转正

- `View/PlayingDetailPage.xaml(.cs)`：移除 `aic:AlbumArtControl`、`xmlns:aic`、`CoverCacheBasePath` 赋值与 Dispose；经典 `ImageSwitcher` 封面改为始终加载（原与 Win2D 封面按开关联斥）
- `Model/SaveSettings.cs`、`ViewModel/AppViewModel.Settings.cs`、`Services/MusicDatabaseService.cs`：移除 `IsWin2dCoverImageControlEnable` 定义与读写；`IsWin2dAnimatedText` 默认 `true`
- `View/SubView/Settings/CoverBackgroundSettingsControl.xaml`、`Strings/*/Resources.resw` ×6：删除"Win2d封面"卡片与 `Win2dCover.Text`；`Win2dAnimatedTitle.Text` 去掉"（实验性）"
- `External/AnimatedWin2dControls` 实现按约定保留，仅主程序不再引用
- 兼容：旧 Settings.json 残留键被 System.Text.Json 默认跳过，下次保存自然消失；曾开启 Win2D 封面的用户回到经典封面

## 2026-09-18 逐字歌词默认启用

- `Model/SaveSettings.cs`、`Model/AppSettings.cs`、`ViewModel/AppViewModel.Settings.cs`：`EnableAdvancedLyricsEffect`（主界面逐字特效）、`IsDesktopLyricsKaraokeEnabled`（桌面歌词逐字渲染器）默认 `false` → `true`
- `DesktopLyrics/DesktopLyricsViewModel.cs`：同步"默认关"注释
- 语义：仅对全新安装生效；存量用户 Settings.json 中显式保存的值优先生效，不做强制迁移
