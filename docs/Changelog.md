# 功能变更记录

新条目加在最上方。

## 2026-09-22 WebDAV 大 DSF 的内存、定位、位流与封面修复

- `AudioPlayer/Playback/AudioRingMemory.cs`、`Ring.cs`、`Session.cs`：网络 PCM/DoP/Native DSD 大环缓冲由会话独占的原生内存承载，停止时在读写锁内释放；拒绝迟到读写、唤醒等待生产者，避免每次切歌的多 MB LOH 分配等待 GC。未增加生产环境强制 GC。
- `Decode/FfmpegHttpInput.cs`、`DsdRawReader.cs`、`PlaybackEngine.Streaming.cs`、`Streaming.cs`、`RemotePlaybackService.cs`：远程 DSF/DFF 按输出设置选择 Native DSD/DoP，匿名桥接 URL 通过扩展名提示保留格式；共用超时、取消、预缓冲/欠载恢复及定位能力，保持 Native → DoP → PCM 设备回退。
- `build/ffmpeg-dsf-seek.patch`、`Libraries/FFmpeg/x64/avformat-63.dll`：从现有 FFmpeg 源码重编译，DSF 按固定块直接定位并使用样本数计算时长，不再为了建立索引遍历未播放音频；构建脚本自动检查/应用补丁。
- `Reader/AudioCoverReader.cs`、`Services/WebDavLibraryService.cs`：按 DSF 头部偏移读取尾部 ID3/APIC；对旧 DSF 空封面缓存做有界重试，复用原图缓存。
- `_tools/PlaybackSwitchRegression`、`_tools/StreamingRegression`：新增大文件定位、精确包位置/EOF、DSD 模式、取消、原生缓冲释放与正式 NativeAOT 连续切换回归；`Player/AudioPlayer.exe` 已更新。
- 验证：NAS 的 1,534,642,268 字节 DSF 封面读取成功；跳到 80% 并预缓冲约 77–78 ms、传输约 5.8–6.2 MB。真实 FiiO ASIO 的 DSD256 Native 和 DSD64 DoP 输出成功；此设备拒绝 DSD256 DoP 所需的 705.6 kHz。测量范围、命令与硬件边界见 `docs/WebDAV接入设计方案.md` 第 15 节。
- 构建：NativeAOT 与主程序 x64 Release 通过；本机缺少符号转换工具，主程序验证通过命令行关闭符号包和包签名（未更改项目发布默认值），现有平台/裁剪等警告保留。

## 2026-09-22 WebDAV 按来源确认 NAS 自签名证书

- `Services/WebDav/WebDavCertificates.cs`、`WebDavTransport.cs`：HTTPS 失败提供证书信息和 SHA-256 指纹；确认后仅放行指定来源地址的同一张有效证书，证书变化需重新确认，过期证书继续拒绝。连接池按信任状态隔离，不修改系统信任或放开其他来源。
- `Model/WebDavSource.cs`、`Services/WebDavLibraryService.cs`：保存来源时持久化证书地址与指纹，目录同步、元数据、封面和播放共用；已有来源默认无证书例外。
- `WebDavConnectionViewModel.cs`、`WebDavConnectionDialog.xaml`、`Utils/WebDavText.cs`、六种语言资源：增加证书详情、确认重连及忘记入口，区分证书未受信任、证书变化与其他 TLS 错误；修改连接信息或关闭窗口会取消测试并拒绝迟到结果。
- `_tools/WebDavRegression`：增加真实 TLS 目录/Range、证书与连接池隔离、过期、来源保存恢复、SQLite 迁移和表单取消回归。NAS 实测为 fnOS 自签名证书且名称不包含访问 IP；固定该证书后 TLS 成功进入 HTTP 认证层，无密码测试返回预期 401。
- 验证：50 项回归、六种语言资源键及格式占位符检查、Release 构建通过；现有编译/裁剪警告仍在。未替换当前运行实例，WinUI 证书确认交互需在新构建验证。

## 2026-09-22 统一 WebDAV 缓存位置并修复播放详情原图

- `Services/WebDavLibraryService.cs`、`Services/WebDav/WebDavCachePaths.cs`、`RemoteAudioCache.cs`：统一使用原有可配置缓存根目录，远程音频和原图分别写入 `WebDav/Audio`、`WebDav/Covers`；缩略图继续共用 `Cache`。取消独立 WebDAV 路径配置，升级及换目录按需重建，旧缓存保留、不批量搬运。
- `Controls/ImageSwitcher.xaml.cs`、`Behaviors/FadeImageBehavior.cs`、`Utils/ToolUtils.cs`、`Helper/PlaybackCoverCache.cs`：展示与取图按同一标识读取同一份远程原图；临时文件完整发布后才刷新封面，不重复保存原图、不重复请求网络。
- `Services/CoverPresentationService.cs`、`SettingsActions.cs`、`ViewModel/Pages/WebDavSourcesViewModel.cs`、关于页及六种语言资源：统一入口更改目录，WebDAV 位置只读展示；切换后刷新封面和缓存占用，清理封面包含远程原图，下载音频仍独立清理。
- `RemoteAudioCache.cs`：换目录使旧写入失效，提交时再次核对代次；旧目录的预留不占用新目录额度，清理新目录不删除旧目录仍在读取的音频。
- `_tools/LyricsCoverRegression`、`_tools/WebDavRegression`：增加同目录原图解析、完整发布、命中、并发、取消/失败、空封面，以及跨目录额度、提交和活动读取回归。
- 验证：歌词/封面 27 项、WebDAV 核心 31 项通过，六种语言设置资源检查通过；x64 安装包构建通过。当前运行中的 Release 实例未替换，统一缓存位置及大封面需使用新构建验证界面。

## 2026-09-22 WebDAV 音乐来源、网络播放与可选缓存

- `Services/WebDav`、`WebDavLibraryService`、`MusicDatabaseService.WebDav`：只读目录同步、稳定来源索引、分批补全标签；FLAC 使用 ATL Stream 跳过封面，其余格式使用有界 FFmpeg 探测，封面优先复用自研读取器。
- `RemotePlaybackService`、`PlaybackCoordinator`、`IpcService`：连接现有 StreamingClient，主程序管理鉴权与 loopback 桥接、暂停/定位/切换和资源收尾；完整音频缓存可复用，自动下载默认关闭、10 GiB 上限。
- `View/AddFolderPage`、`WebDavSourcesControl`：统一音乐来源标题、添加菜单及本地/WebDAV 卡片样式；音乐库增加来源筛选，歌曲/收藏/歌单/分组列表增加来源图标列。
- `View/MainPage`、`View/SubView/Settings/AboutSettingsControl`：网络标识放到播放栏歌曲信息与音频格式同一行，状态以提示显示；缓存设置放到关于页的可展开卡片，不增加独立设置分类。
- `Model/Music`、相关详情/转换/导出入口：远程文件只读，收藏、歌单、统计继续使用统一 Music.Id；本地扫描不清理远程记录，相同专辑名按来源区分。
- `Strings/*/Resources.resw`：新增界面与错误信息覆盖六种语言，程序取词采用独立资源键。
- `Player/AudioPlayer.exe`、`TrimmerRoots.xml`、项目文件：更新 NativeAOT 播放器，避免远程结束重复走旧通知；保留 SQLite 模型与命名管道依赖供裁剪后的应用使用。
- 验证：x64 安装包构建成功；OpenList 实际 224 首元数据通过；WebDAV 核心 22 项、网络播放器 39 项、本地切换 281 项、共享曲库和歌词封面回归通过；实际界面验证来源添加、扫描、播放/暂停和约 33 MiB 缓存落盘。NAS、广域网与近 100 GB 长时间负载尚未验证，性能测量范围见设计文档第 14 节。

## 2026-09-21 移除 Atmos / 5.1 状态卡的“恢复普通播放”按钮

- `View/SubView/Settings/GeneralSettingsControl.xaml`：删除 Atmos 直通与 5.1 环绕状态提示条上的按钮及随之失去意义的 `ContentAlignment="Right"`，状态文本保留，关闭功能直接用各卡片自身的 toggle。
- `ViewModel/AppViewModel.Atmos.cs`、`ViewModel/AppViewModel.Surround.cs`：删除 `UseOrdinaryPlaybackCommand` / `UseOrdinarySurroundPlaybackCommand`，实现只是把对应开关设为 false，与 toggle 完全等价；同步移除不再使用的 `CommunityToolkit.Mvvm.Input` using。
- `Strings/*/Resources.resw`：移除 6 种语言的 `AtmosUseOrdinary`、`SurroundUseOrdinary` 资源键。

## 2026-09-21 AudioPlayer 网络播放基础

- `External/AudioPlayer`、`External/BassPlayerIpc.Shared/Streaming.cs`：参考 Plugins 最后提交 df6f9d99，加入 HTTP(S) 描述符、鉴权请求头、独立管道客户端、异步准备、播放/暂停、Range 定位、URL 刷新及缓存状态，为 WebDAV GET 播放准备接口。
- `Playback/Session.cs`、`Playback/Ring.cs`、`Decode/PcmDecoder.cs`：有界预缓冲和断流恢复、原生 I/O 超时取消、失败与自然结束分离；网络源不触发本地响度扫描，退出阻止新请求并等待准备任务收尾。
- `Playback/PlaybackEngine.Streaming.cs`：补齐停止时恢复计划失效、在途准备并发上限、seek ID 同步及越界拒绝；刷新保留位置和暂停意图。
- `Libraries/FFmpeg/x64`、`build/ffmpeg-network.sh`：使用 Plugins 四个 DLL；实测同为 9.0.1，编解码器/封装器/解封装器列表相同，输入协议新增 httpproxy；更新构建来源与 SHA-256。
- `Player/AudioPlayer.exe`：更新 NativeAOT 发布产物；`External/AudioPlayer/README.md` 补充接入示例和能力边界。暂不包含 WebDAV UI、目录浏览或账号管理。
- 验证：主程序 x64 构建及播放器 NativeAOT 发布成功；本地播放回归 281/281，网络集成 39/39；真实 AOT 进程网络集成覆盖 HTTP、Range、鉴权头描述符、401、截断、重试、超时、TLS 不可信证书拒绝、实际设备播放、暂停刷新、自然结束与打开期间退出；未验证真实 WebDAV 服务和 HTTP 代理服务器。

## 2026-09-21 FFmpeg DLL 换为支持网络播放的最小化构建

- `Libraries/FFmpeg/x64/*.dll`：基于同一 n9.0.1 源码（commit bf1b838f）重编，去掉 `--disable-network`，协议在 file 基础上新增 http/https/tcp/tls，TLS 用 Windows 原生 SChannel；demuxer/decoder/encoder/muxer/parser 清单与原版完全一致（schannel 会自动带入 dtls/udp，为 configure 上游行为）。
- `Libraries/FFmpeg/build-audio-net.sh`：新增网络版构建脚本，与原 `build-audio.sh` 仅上述三处差异；`Libraries/FFmpeg/x64/BUILD_INFO.txt` 更新工具链（MSYS2 UCRT64 gcc 16.2.0，D:\code\msys64）、SHA256 与验证记录。
- 验证：PlaybackSwitchRegression 281/281；本地 HTTP（E-AC-3 5.1 M4A）与公网 HTTPS MP3 实际经 libavformat 打开解码，旧 DLL 对 http:// 正确拒绝。License 不变（LGPL-2.1+，schannel 为系统组件）。

## 2026-09-21 退出时取消在途歌词请求

- `Services/LyricsLoader.cs`、`Services/LyricsRefreshService.cs`：将应用停止令牌与切歌取消合并；每次解析在实际结束后释放自身 CTS，取消后停止后续搜词，不再让退出等待完整网络超时与回退链路。
- `WebService/LrcService.cs`、`External/Lyricify.Lyrics.Helper`：网易云/QQ 搜索、歌词下载和 HTTP 读写贯通取消；保留原调用重载，取消不触发备用搜索，释放请求内容和响应。
- `_tools/LyricsCoverRegression`：新增挂起请求取消、退出等待实际清理、取消后重试，以及网易云新搜索和两家歌词下载的取消回归。

## 2026-09-20 更新对话框警告精简并补充系统美化软件不兼容

- `Strings/*/Resources.resw`：`RtssWarningTitle` 放宽为覆盖 FPS 监控与系统美化两类注入软件；`RtssWarningBody` 精简为一段，保留「桌面歌词＋着色器背景」与 DXGI 挂钩冲突的触发条件，并补充 Windhawk、StartAllBack 等注入式系统美化软件同样可能导致崩溃，均建议关闭或将本程序加入排除列表。全部 6 种语言同步，键名与 XAML/GetString 未变。

## 2026-09-20 歌词 Helper 迁移到可剪裁的 System.Text.Json

- `External/Lyricify.Lyrics.Helper`：移除 Newtonsoft.Json，使用源生成 JSON 元数据迁移各提供商、KRC/YRC/Spotify/Musixmatch；兼容数字字符串、布尔值、请求转义与浮点输出，Musixmatch 使用可释放的 JsonDocument。
- `WinUIMusicPlayer.csproj`：移除 Lyricify.Lyrics.Helper 的 TrimmerRootAssembly；库启用剪裁分析，外部自定义 DTO 可传入 JsonTypeInfo。
- `_tools/LyricsJsonRegression`：保存迁移前 191 个模型、请求载荷及解析/生成器输出基线，禁用反射并验证全剪裁发布；公共 ToJson 缩进参数改为 bool，未指定类型的 JSON 对象改为 JsonElement，其他兼容边界与测量见其 README。

## 2026-09-20 修复外部导入重开、文件夹显示与并发创建

- `Services/OneShotPlaybackService.cs`：每次显式打开重新解析库内身份，移除后重开和失败后重试不再复用旧结果；通过文件夹 ViewModel 统一发布导入状态。
- `ViewModel/Pages/AddFolderViewModel.cs`：导入歌曲按 Id 去重发布，同步数据库中的文件夹行、计数与空状态，首次导入立即显示虚拟文件夹。
- `Services/MusicDatabaseService.ExternalImports.cs`：从数据库主文件移出外部导入方法供真实代码回归复用；虚拟文件夹查询与创建在同一事务中完成，避免并发查空后重复插入。
- `_tools/FolderScanRegression`：编译生产解析与导入代码，覆盖移除后同路径重开、失败后重试、首次发布、重复发布计数和 30 轮并发创建；平台桩不替代真实 WinUI 文件激活与派发验证。

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
