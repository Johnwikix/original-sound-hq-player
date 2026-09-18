# 功能变更记录

新条目加在最上方。

## 2026-09-18 一次性外部文件优先复用库内同路径曲目的已存歌词

- `Services/MusicDatabaseService.cs`：新增 `GetLyricsByPathAsync`——按路径 NOCASE 匹配库内条目（走 `IX_Music_Path_NoCase` 索引）并取其已保存歌词；数据库在引擎就绪前必已初始化，无需新增启动顺序
- `Services/LyricsRefreshService.cs`：一次性分支歌词优先级调整为与库内播放对齐——文件旁本地 → 库内同路径已存歌词（先 KRC 后 LRC，传入非空原文只解析不触发在线搜索）→ 内嵌 → OneShotLyricsCache → 在线搜索；库内命中的原文不写入路径缓存，仅本次在线新搜到的结果写回

## 2026-09-18 修复连续切换一次性外部文件时迟到歌词覆盖当前曲目

- `ViewModel/AppViewModel.cs`：歌词迟到守卫从按 `Music.Id` 匹配改为递增票据（`Interlocked`/`Volatile`）——一次性外部曲目 Id 均为 0，按 Id 匹配会放过上一首的迟到结果，覆盖正在播放曲目的 `UILyrics`

## 2026-09-18 文件关联一次性播放：系统"打开方式"入口直接播放外部文件

- `Package.appxmanifest`：新增 `windows.fileTypeAssociation`，注册 15 种音频扩展名（与 `ToolUtils.MusicExtensions` 一致），应用出现在系统"打开方式"候选
- `Services/OneShotPlaybackService.cs`（新增）：一次性播放唯一状态源——`CaptureActivationPath` 最早捕获激活文件（MSIX 文件激活 + unpackaged 命令行回退），后台解析出未入库 Music（Id=0）仅替换 `CurrentPlayingMusic` 播放并携带内嵌歌词；引擎就绪采用生命周期/就绪属性事件驱动派发（无固定延时），迟到回调按请求代差丢弃；失败 Toast 提示
- `Services/LyricsRefreshService.cs`：未入库曲目（Id=0）歌词链路补全——文件旁本地歌词 → 内嵌歌词 → KRC/LRC 在线搜索，全部不查询/写入数据库、不递增播放计数；在线搜索仍受"自动获取歌词"设置与熔断器约束；`Model/Music.cs` 新增 `[Ignore] EmbeddedLyrics` 内存字段承载
- `Services/OneShotLyricsCache.cs`（新增）+ `Helper/AppJsonSerializerContextHelper.cs`：外部文件独立歌词缓存——按路径 SHA-256 哈希存 JSON（`LocalFolder/OneShotLyricsCache`，与数据库无关），命中免在线搜索、搜到即写回；明文路径校验防哈希碰撞、临时文件原子替换、300 条上限按最后写入时间淘汰，读写失败静默不影响播放
- `App.xaml.cs`：`OnLaunched` 最早期捕获激活路径；第二实例带路径转发，第一实例立即 `Begin` 解析（与 Host/IPC/数据库初始化并行）；DI 注册服务
- `Helper/SingleInstanceHelper.cs`：`ActivateExistingInstance` 增加 `filePath` 参数，经 `WM_COPYDATA`（魔数 + UTF-16 路径）同步转发给现有实例主窗口
- `MainWindow.xaml.cs`：`NewWindowProc` 新增 `WM_COPYDATA` 分支——WndProc 内同步拷贝负载（SendMessage 语义要求）后转 UI 线程 `PlayNow`
- `Services/StartupCoordinator.cs`：引擎轨首推前探询一次性文件，解析已完成则内核直接加载外部文件（省一次恢复曲加载）；注册服务清理
- `Services/BassPlayerCommandService.cs`：`AutoPlayNextTrack` 列表循环分支补空队列守卫——一次性曲目曲终按现有 `index=-1→nextIndex=0` 语义从播放队列第一首继续；队列空时结束播放防取模零异常
- `Services/PlaybackStatsService.cs`：`StartSession` 跳过未入库曲目（Id≤0），不产生统计孤儿记录
- `Services/PlaybackStatePersistence.cs`：`LastPlayedMusicId` 仅在当前曲已入库（Id>0）时写入，一次性曲目不破坏下次启动的当前曲恢复
- `Strings/*/Resources.resw` ×6：新增 `OneShotOpenFailedTitle`/`OneShotOpenFailedContent`
- 行为：外部文件不入库、不入播放列表、不写统计；歌词为内存态（本地/内嵌/在线，不落库）；单曲循环仍重播该文件；手动"下一首"进入队列播放；多选文件只播第一个
- 兼容：无数据库/设置结构变化；MSIX 部署后"打开方式"候选生效需重装/更新部署包

## 2026-09-18 评审回修：扫描变更判定计入 UPDATE，播放命令恢复退出守卫

- `Services/MusicDatabaseService.Scanning.cs`：`CommitScanBatchAsync` 返回新增行与 UPDATE 命中行数，`RescanFolderCoreAsync` 一并计入变更数——修复既有文件仅元数据更新（外部改标签/重写）被误判"无变更"、启动后 UI 不刷新且 `UpdateTime` 已前移导致后续启动不再补偿的问题
- `Services/PlaybackCommands.cs`：`CanPlay` 恢复 `_lifecycle.IsReady` 守卫——`IsPlaybackEngineReady` 只在置位时包含 Ready，退出转 Stopping 后不复位，此前退出窗口期播放命令仍可达后端；统一成员缩进
- `_tools/FolderScanRegression/RegressionSuite.cs`：修正断言（插入时已保存精确文件时间，紧邻扫描本就无变更），新增"文件时间戳变化→报告有变更"正向用例
- `_tools/LifecycleRegression/Program.cs`、`Stubs.cs`：Ready 后置 `IsPlaybackEngineReady`（对齐引擎轨置位时机），桩属性改为可通知
- `docs/ApplicationLifecycle.md`、`docs/LegalAndStartup.md`：启动文档同步"音频进程与 IPC 先于协议拉起"新语义；历史验证记录标注改序时间点，改序后实机复验未执行
- 兼容：仅修改变更判定与命令守卫，无数据库结构/设置迁移；两个回归套件实跑全绿、主工程构建 0 错误

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
