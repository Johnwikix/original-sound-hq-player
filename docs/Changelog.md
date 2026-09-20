# 功能变更记录

新条目加在最上方。

## 2026-09-20 修复双击导入后歌曲/专辑/艺术家列表与播放队列不刷新

- `Services/OneShotPlaybackService.cs`：入库与播放派发存在竞速——派发时另行按路径查库，而解析任务内的入库写仍在飞行，查空即误入一次性分支（`PlayMusic` 只换当前曲：不发布 SongsSource/页面投影、不建文件夹队列）；歌曲/专辑/艺术家列表要等重启后的全量加载才正确。修复：删除派发时查库，分支一律以 await 后的解析结果 Id 判定（Id>0=库内行，统一发布+`PlayMusicWithFolderQueue`）；固定盘先查库再解析元数据（库内已有文件免重复解析）；发布路径改为顺序等待扫描批发布完成（SongsSource/ListSongs/文件夹计数）后触发 `NotifySongsSourceChanged`，当前页投影按库版本重建、导入即时按序可见。
- 验证：主工程构建 0 错误；FolderScanRegression（含外部导入场景）与 LifecycleRegression 全部通过；OneShot 派发竞速需真机复测（首装双击导入立即出现在歌曲/专辑/艺术家页且进入当前播放队列、连续导入多首逐一可见）。

## 2026-09-20 双击打开的外部文件入库为库内条目（外部导入虚拟文件夹）

- `Services/OneShotPlaybackService.cs`：解析阶段判定位置——固定本地盘且库内无同路径行时经 `AddExternalFileAsync` 入库并返回库内权威行，播放、统计、歌词、当前曲存档均为标准库内语义；可移动盘/网络盘/UNC 及入库失败回退原一次性播放（Id=0 不写库不统计，`OneShotLyricsCache` 继续服务）；引擎首推探询拿到的即库内行，保留省一次恢复曲加载的优化；首次入库条目经扫描批发布路径增量进 SongsSource/文件夹计数。
- `Services/MusicDatabaseService.cs`：新增 `WhenInitialized`（早于 Host 启动的一次性解析在入库前等待）与 `AddExternalFileAsync`（确保虚拟行存在 + 复用提交批核心，事务内同路径查重防并发扫描/转换重复落库，失败按路径回查）；`Initialize` 失败以异常完成信号。
- `Services/MusicDatabaseService.Scanning.cs`：外部导入虚拟文件夹（`Folder.Type="external"` 哨兵行）管理——归属按路径现算（不在任何本地扫描根内的行），`GetFoldersWithSongCountsAsync` 现算虚拟行计数，`CheckFolderBeforeAdd` 重叠判定排除虚拟行；`RescanFolder`/`RemoveFolder` 对虚拟行改为存在性对账/按归属移除；新增 `ReconcileExternalImportsAsync`（盘根不可达整组保留、确认缺失行连同歌词删除）。
- `Services/InitialFileScan.cs`、`AutoRescanService.cs`、`LibraryWatcherService.cs`：枚举/监视跳过虚拟哨兵行；启动扫描末尾统一执行外部导入对账。
- `Services/LibraryPath.cs`：新增 `IsFixedLocalDrive`（可移动/网络/UNC/光驱/无法判定返回 false，与 UsbDeviceMusic 设备音乐体系保持边界）。
- `Model/Folder.cs`：`Type` 常量（`TypeLocal` 保持存量字面量、`TypeExternal`）、哨兵路径常量、`IsExternalImport`/`CanOpenInExplorer` 派生属性。
- `ViewModel/Pages/AddFolderViewModel.cs`、`Services/FolderCommands.cs`、`View/AddFolderPage.xaml`：`ApplyBatchAsync` 改 internal 并同步虚拟行计数；虚拟行显示名在展示层注入资源（库内不固化本地化文本）、隐藏"打开所在位置"；移除确认按类型区分文案。
- `Strings/*/Resources.resw`（六语言）：新增 `ExternalImportsFolderName`、`RemoveExternalImportsTitle`。
- 行为：从资源管理器双击固定盘音乐文件 = 入库 + 按文件夹页语义播放（同文件夹入队列）；所在目录之后加入扫描不重复（归属移交扫描根）、移除扫描根随路径删除、文件从磁盘删除由启动对账清理；多选文件仍只取第一个；U 盘/网络路径双击维持不入库的一次性播放。
- `_tools/FolderScanRegression`：新增外部导入回归（归属现算/移交、扫描根加入去重与移除、对账删除/保留、虚拟行移除）；`Program.cs` 末尾目录清理加句柄晚释放重试兜底，消除偶发非零退出。`_tools/LifecycleRegression`：桩 `Folder` 补 `IsExternalImport` 对齐生产模型。

## 2026-09-20 悬停滚动开关默认关闭并更名

- `State/AppearancePreferencesState.cs`、`Model/SaveSettings.cs`：`IsHoverScrollEnabled` 默认值由开改为关；设置文件中已保存该键的用户不受影响，仅新装机或无该键的存档默认关闭。控件侧 `AnimatedTextBlock.IsHoverScrollEnabled` 本就默认关，保持一致。
- `Strings/*/Resources.resw`（六语言）：`AnimatedTextHoverScroll` 显示名改为「Win2d动画文本悬停滚动」，作用域由标题表达——AutoScrollView 包裹的普通 TextBlock（回退路径与各列表页）的悬停滚动是默认常开、不受此开关控制的既有行为，避免名称误导；描述保持一句行为说明不变。
- `_tools/SettingsPersistenceRegression/Program.cs`：默认值断言改为默认关闭，改为验证显式开启值可往返保存；已运行通过。
- 范围说明：该开关唯一运行时消费点是 `View/PlayingDetailPage.xaml` 的 `AnimatedTextBlock` 绑定；同页 Win2D 文本关闭时的 `AutoScrollView` 回退路径及各列表页的悬停滚动不受它控制（既有行为，未改动）。

## 2026-09-20 继续迁移窗口、队列与展示生命周期

- `State/`、`Services/HotKeyService.cs`、`ShellService.cs`、`OutputDeviceService.cs`：共享快捷键、窗口和设备状态；原生注册、枚举与解绑移出 AppViewModel，退出等待枚举结束并屏蔽迟到结果。
- `Services/SettingsCoordinator.cs`、`SettingsSnapshotFactory.cs`、`SettingsActions.cs`：设置效果和保存快照解除 AppViewModel 反向依赖，目录操作由设置命令服务承担；保留旧绑定名称和默认值。
- `State/PlaybackQueueState.cs`、`Services/PlaybackCoordinator.cs`、`Model/SavePlayState.cs`：为重复歌曲建立独立条目身份，统一前后切歌及队列双击入口；保存随机顺序与游标，旧文件或不匹配的存档安全回退；库刷新批量保留条目身份。
- `Services/LibraryBrowseCoordinator.cs`、`LibraryProjectionService.cs`：迁移库展示集合与刷新调度；UI 捕获字段快照，后台执行列表搜索/排序，每个目标合并请求、全局串行计算，停止后不发布结果，池数组可靠清空归还。
- `Services/CoverPresentationService.cs`、`LyricsLoader.cs`、`LibraryTrackActions.cs`：封面、歌词展示及网络歌词任务移出根/浏览 VM，并纳入任务屏障；菜单打开时才准备歌单和 USB 子项，不再预先创建所有页面 VM。
- `Services/EditorSessions.cs`、DSP/卷积/频响 VM、`SystemMediaControlsService.cs`：退出等待编辑器提交、导入/预设操作与频响计算；SMTC 只提交当前版本元数据，显式持有并释放封面原生流。
- 验证：七组回归通过（播放切换 281/281）；本轮新增共享快捷键、重复条目恢复、编辑器等待/失败隔离、查询合并/停止发布回归。用户已通过上一轮人工验收；本轮 WinUI 菜单、设置即时退出、真实设备/SMTC 和 Release GC 对照仍需验收。

## 2026-09-20 动画文本空字形回调修复

- `External/AnimatedWin2dControls/AnimatedWin2dControls/Controls/AnimatedTextBlock/Internals/ShapedText.cs`：跳过 `null` 和空字形数组，修复切换文本时 `DrawGlyphRun` 访问 `glyphs.Length` 引发的空引用异常。

## 2026-09-20 共享状态迁移与异步收尾

- `State/`、`ViewModel/AppViewModel*.cs`：新增单例 `AppState`，迁移播放进度/队列、浏览状态、输出状态及 71 个分域偏好；旧绑定通过同一实例转发通知，保留默认值与现有布局。
- `Services/PlaybackCoordinator.cs`、`PlaybackProgressService.cs`、`ApplicationTasks.cs`、`ShutdownCoordinator.cs`：提取选曲用例与进度轮询，拒绝退出后的播放，等待在途任务，退出步骤记录名称与耗时。
- `Services/SettingsCoordinator.cs`、`SettingsSaveQueue.cs`、`SettingsSnapshotFactory.cs`：分离偏好副作用和持久化快照，合并设置写入及异常观察任务；退出提交防抖期间的最终值。
- `DesktopLyrics/DesktopLyricsViewModel.cs`：桌面歌词公共开关使用共享状态，修复托盘/快捷键与设置页逐字开关不同步。
- `State/PlaybackQueueState.cs`、`Services/LibraryQueries.cs`、`LibraryProjectionService.cs`：随机追加同时更新规范队列和播放顺序；库索引按版本重建，过滤/分组缓存查询键并延后隐藏页刷新，引用池数组归还时清空。
- `ViewModel/StatsViewModel.cs`：慢查询期间保留最新筛选请求，拒绝旧结果，离页停订阅和刷新；查询发布保持 UI 上下文。
- `Services/UsbExportCoordinator.cs`、`Helper/UsbWriterHelper.cs`：USB 操作独立身份和退出屏障，只登记成功复制/转换的实际格式；取消在当前文件实际完成后停止下一项。
- 详情页 VM 改用可等待命令，快照化删除/重排输入，离页解绑；文件夹 VM 构造不查库，激活加载并等待进行中操作收尾。
- `_tools/SharedStateRegression`：覆盖共享通知、随机追加、合并写盘/失败重试、停止屏障、真实文件复制及统计页异步竞态。WinUI 实机交互和 Release GC/延迟测量尚未执行；完整迁移状态见 `docs/ServiceRefactoringPlan.md`。

## 2026-09-20 文件夹兼容回退与退出异常隔离

- `Services/FolderAccessService.cs`、`ViewModel/Pages/AddFolderViewModel.cs`、`App.xaml.cs`：提取文件夹平台交互服务，按路径打开目录并补 Explorer 回退；选择器 COM/不支持异常时回退 HWND 绑定的 WinRT 选择器，取消不重复弹窗。
- `Services/AppLifecycle.cs`、`Services/ShutdownCoordinator.cs`：退出时隔离取消及状态订阅者异常，继续保存与清理，避免停留在 Stopping 而不再执行收尾。
- `_tools/LifecycleRegression`、`_tools/FolderScanRegression`：新增退出失败与选择器回退回归；修正播放意图测试的引擎就绪前置条件。
- `TODO.md`、`docs/ServiceRefactoringPlan.md`：记录分阶段重构方案及验收边界；第 3 项已于 2026-09-20 经用户确认验收，原生退出崩溃仍待定位。

## 2026-09-19 5.1 自动独占与输出状态布局调整

- `External/AudioPlayer/Playback/PlaybackEngine.Surround.cs`、`PlaybackEngine.cs`、`Decode/PcmDecoder.cs`：共享偏好下仅兼容 5.1 PCM 曲目自动切独占，保留音量和静音；ASIO 保持原模式，失败在同设备回退普通 PCM，设备变化暂停，普通曲目恢复原输出偏好；Atmos 优先且回退不触发二次独占。
- `External/AudioPlayer/Interop/WasapiInterop.cs`：Atmos 与 5.1 共用稳定端点解析，显式设备失效时不改用默认扬声器。
- `External/BassPlayerIpc.Shared`、`Services/IpcService.Atmos.cs`、`ViewModel/AppViewModel.Surround.cs`：发布 5.1 实际状态并复用统一失败系统通知；DSP 状态邮箱升级为 v8，主程序与 `Player/AudioPlayer.exe` 配套更新。
- `View/SubView/Settings/GeneralSettingsControl.xaml`、`ViewModel/AppViewModel.Atmos.cs`、`Strings/*/Resources.resw`：去掉 Atmos 状态文案的强制换行，状态文字与操作按钮左右排列，窄窗口自然折行；增加 5.1 状态与关闭入口，补齐六种语言。
- 验证：播放回归包含自动切换、音量/静音、暂停保进度、同设备回退、无效端点、ASIO 及 Atmos 组合；WinUI 构建与资源键静态校验。真实六声道出声、通知横幅及新布局实机视觉尚未验证。

## 2026-09-19 Atmos 自动独占直通与统一系统通知

- `External/AudioPlayer/Playback/PlaybackEngine.Atmos.cs`、`PlaybackEngine.cs`、`Interop/WasapiOutput.cs`：兼容曲目临时使用 WASAPI 独占，保留普通输出偏好；固定目标端点协商格式，失败保进度回退同设备 PCM，断开设备暂停，避免重复抢占；能力查询与初始化共用超时及资源收尾。
- `External/BassPlayerIpc.Shared`、`Services/IpcService.Atmos.cs`、`ViewModel/AppViewModel.Atmos.cs`、`View/SubView/Settings/GeneralSettingsControl.xaml`：新增可选 Atmos 专用设备、实际状态和恢复普通播放入口；ASIO 需明确指定设备，旧配置沿用当前输出；六种语言补齐独立资源键。
- `Services/NotificationService.cs`、`App.xaml.cs`、`Services/MusicDatabaseService.Metadata.cs`：统一系统通知单例入口，失败通知区分 PCM 回退和停止，30 秒同类去重、有界缓存和异常隔离，退出后拒绝迟到通知。
- `Player/AudioPlayer.exe`：同步发布更新后的 AOT 播放端；DSP 状态邮箱升级，主程序与播放端须配套更新，旧设置载荷仍可读取。
- 验证：主程序构建、播放切换回归、通知网关并发/失败/退出测试、设置持久化、元数据通知回归和 Release 许可门控；真实 HDMI 功放、系统通知横幅及界面设备交互仍需实机验证。

## 2026-09-19 合并动画文字、增加悬停设置并修复混排动画收尾偏移

- `View/SubView/Settings/CoverBackgroundSettingsControl.xaml`、`ViewModel/AppViewModel.Settings.cs`、`Model/SaveSettings.cs`、`Services/MusicDatabaseService.cs`：在 Win2D 动画文本块下增加悬停滚动开关，即时生效并持久化；旧配置默认开启，保持原页面行为，六种语言补齐独立资源键。
- `View/PlayingDetailPage.xaml(.cs)`、`Utils/BindUtils.cs`：标题与两行专辑/艺术家改为一个 AnimatedTextBlock，保留字号、字重、透明度及固定行高；整组内容一次更新，统一推进动画效果。
- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：新增不可变 Document/Paragraph 输入，共用 Canvas 和动画进度；各截断行独立悬停滚动，切换动画优先，格式变动和卸载释放缓存。
- `External/AnimatedWin2dControls/.../AnimatedTextBlock/Internals`、`Effects`：逐字效果复用完整排版的字形、回退字体和基线，统一静态/动画的像素对齐；修复重复 Stay/Move 操作，保留段落偏移和彩色符号绘制。
- `_tools/AnimatedTextRegression`、`_tools/SettingsPersistenceRegression`：增加单 Canvas、多样式、八种效果、混排末帧图像对比、DPI/RTL/截断/emoji、开关绑定及设置兼容性回归。

## 2026-09-19 修复动画文字在 x:Load 初始化时消失

- `View/PlayingDetailPage.xaml`：字号改为带正值回退的常规 Binding，避免 x:Load 创建控件时生成的 x:Bind setter 写入默认 0，触发 WinUI 参数错误并阻止控件进入可视树；保留 ViewModel 字号联动与固定行高。
- `_tools/AnimatedTextRegression/LayoutRegressionPage.xaml(.cs)`：新增真实编译 XAML 的嵌套 Grid、x:Load、绑定和 Loaded 字号更新用例，验证控件进入可视树、非零尺寸与绘制完成。

## 2026-09-19 修复跨字体切换导致文字动画中断和布局抖动

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：文本过渡中尺寸变化时重建动画布局，不再直接切入 Idle；新增 `LineHeight`，默认 0 保留自然行高，正值固定行高与基线。
- `View/PlayingDetailPage.xaml(.cs)`：动画文字字号统一绑定 ViewModel，并按字号的 1.4 倍向上取整设置行高，稳定中英文切换及两行专辑/艺术家信息的布局。
- `_tools/AnimatedTextRegression`：复现“祝融 → All In My Head”自然行高 31 → 32 DIP 导致动画中断；补充固定行高、基线与跨行数的 Fade/Default/Wipe 回归。

## 2026-09-19 修复 AnimatedTextBlock 两行信息的悬停滚动

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：支持专辑与艺术家在同一控件内显式换行；截断行按各自长度滚动，短行保留对齐与基线，切换动画仍优先，复位时统一释放各行布局。
- `_tools/AnimatedTextRegression`：补充 CRLF/LF、单行溢出/双行溢出、空行、对齐、RTL 和两行切换动画的真实 WinUI 回归。

## 2026-09-19 AnimatedTextBlock 自动测量与悬停滚动

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：按 Win2D 文字布局测量自身尺寸，统一依赖属性变更处理，恢复卸载后重新加载的资源与事件。
- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：新增 `IsHoverScrollEnabled`（默认关闭），单行横排文字实际截断时悬停往返滚动；切换动画优先，移出、文字/格式/尺寸变化及卸载时复位。
- `View/PlayingDetailPage.xaml`：启用动画文字悬停滚动，普通文字分支改为 Collapsed，不再用透明文字撑高。

## 2026-09-18 外部文件路径匹配库内条目时直接按库内曲目播放

- `Services/MusicDatabaseService.cs`：新增 `FindMusicByPathAsync`——按路径 NOCASE 匹配库内条目（走 `IX_Music_Path_NoCase` 索引）返回完整 Music；数据库在引擎就绪前必已初始化，无需新增启动顺序
- `Services/OneShotPlaybackService.cs`：播放派发时先按路径查库——命中则播放库内条目（优先取 SongsSource 实例与库内列表/收藏同源，索引未同步时退回数据库行实例），统计、歌词、当前曲存档均为标准库内语义；未命中才等待外部解析走一次性播放（Id=0、不写库不统计）；查询失败按未命中回退
- `ViewModel/Pages/MusicBrowseViewModel.cs`：新增 `PlayMusicWithFolderQueue`——库内命中时播放队列替换为同文件夹曲目（`LastLevelFolderPath` 聚合，语义与文件夹页播放一致，沿用库内顺序），从匹配曲目开始；随机播放模式经 `SequentialPlayingList` 赋值自动洗牌；同文件夹条目尚未同步进 SongsSource 时不替换队列仅替换当前曲
- `Services/LyricsRefreshService.cs`：一次性分支只服务纯外部文件（本地 → 内嵌 → OneShotLyricsCache → 在线搜索），移除按路径取库内歌词阶段（匹配文件已改走标准库内链路，该阶段不可达）
- 行为：匹配文件从资源管理器打开 = 按文件夹页语义播放库内对应曲目（队列换为同文件夹歌曲、曲终接续文件夹顺序）；纯外部文件行为不变

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
