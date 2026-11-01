# 首页轮播、页内详情与当日独立审查记录

日期：2026-10-31。最终实现保留 Frame 与既有跨页动画；本功能不导航 Frame，不修改当前自编译 CsWinRT 或播放器协议。

## 2026-10-31 最新：高 DPI 边框缩放卡死与三项 P2 修复

### 缩放卡死：用户日志与原生失败对照

用户确认触发方式是拖动窗口边框改变大小。指定日志 `C:\Users\90684\Documents\OriginalSoundPlayer\Logs\WinUIMusicPlayer-20261031_001.log` 在 18:57:45.097、18:58:13.101、19:17:43.623 记录 LayoutCycleException。稍后的 PipsPager 0x88000FA8 出现在布局异常之后，不据此改分页控件或 CsWinRT。既有 App.UnhandledException 会记录并标记 Handled，解释了进程仍活着而画面停留；本轮没有靠吞掉布局异常或改变全局异常策略处理问题。

此前夹具未指定生产 app.manifest，实际只按 100% DPI 运行，遗漏了本机主屏 150% DPI。两套原生夹具现补上同一 PMV2 清单及与主项目一致的 runtime-async 功能设置。补齐后，修改前夹具在连续缩放第 141 步、物理窗口 1180×805 处复现同一个原生异常。

失败环形记录显示：父 ScrollViewer.ViewportWidth 始终为 722.666687 DIP，RasterizationScale 为 1.5，而 LayoutGrid.ActualWidth 在 674.666687 / 675.333313 DIP 之间反复切换，正好相差一个物理像素；每次 SizeChanged 又用该结果反写行高和 Star 列权重，构成持续布局反馈。证据保存在 `artifacts/home-polish-batch2/resize-dpi-before/resize-failed-layout.json` 与 `resize-last-step.json`；异常退出的运行不计作通过。

最终 FeaturedCarousel 在 MeasureOverride 中使用父级分配的 availableSize.Width，扣除内部 32 DIP 溢出留白后计算 16:9；数据变化只 InvalidateMeasure，SizeChanged 仅定位箭头。分栏时主图列使用该分配宽度，右侧占用剩余空间；无拼贴仍跨 Star 列填满。比例、间距、居中裁切和箭头外伸坐标沿用既有要求。

只切断 ActualWidth 反馈但保留 Star 主列的中间版本，虽然通过缩放循环测试，仍在缺图切换中出现封面宽 408.666656 / 409.333343 的一像素变化，触发 MusicCoverControl 原有尺寸变化停止动画。固定主图的测量分配列后，同一原生动画控制器断言立即通过，封面事件诊断没有再记录该尺寸摆动。把封面 SizeChanged 移到外层的实验没有解决此问题，已撤回，MusicCoverControl 本轮没有最终修改。对应诊断记录为 `p2-dpi-size-probe`、`p2-dpi-cover-fixed`、`p2-dpi-fixed-columns` 目录。

既有居中测试曾在图片刚加载、700ms 进入动画仍在进行时检查 TransformToVisual，记录到 X≈457 DIP；该位置不能用于静态居中判定。现保持动画验证不变，另在进入动画结束后检查真实 Image 的 UniformToFill、居中及尺寸，输出 `carousel-centering.json`。未放宽原有 1 DIP 居中与 1.25 DIP 比例误差标准。

### 三项 P2：实现、失败基线与所有权

1. **页内详情与底栏跨链导航**：MainPage 的详情及导航方法集中到同一 partial 文件，供独立夹具链接真实实现；在主 Frame 已经是 MusicBrowsePage 时，也先 Dismiss 临时详情、清除待显示请求及旧关闭回调，先还原来源返回状态，再让内层目标页发布状态。没有新增全局详情实例或缓存。失败基线为 `artifacts/song-details/p2-navigation-before.log`，实际 footer 命令仍被详情遮挡；修复后覆盖专辑/歌手入口、普通返回、同一 UI 回合先关闭再跨链、播放页切换期间待显示请求被新导航取消。
2. **旧属性窗口冷封面读取**：WindowArtworkSession 仅将原始字节读取委托 Task.Run，await 继续回到调用方 UI 上下文创建 BitmapImage 与发布 Updated。四个生产调用委托均核对为字节 I/O，不操作 XAML。关闭会取消令牌并断开图片/字节/事件引用；取消不冒充同步磁盘工作已经停止，LoadAsync 等待真实读取收尾，迟到数据不能发布。失败基线 `p2-window-before.log` 证明读取发生在 UI；修复后用真实后台阻塞关卡、UI 心跳、原生 BitmapImage、忽略取消的读取及关闭后释放验证。没有改变图片解码大小或增加长期图片缓存。
3. **逐首导入的重复匹配**：PlaybackSelectionTracker 为条目变化合并一次 Dispatcher 同步，缓存每个 tracker 的两个回调；播放/切歌和队列状态在 UI 上仍立即同步，不增加自动滚动。排队任务不捕获曲目或索引快照，执行时读取最新状态；离树解绑外部订阅，迟到旧 Unloaded 在重新 Loaded 后不解绑新订阅，排队任务在离树时不触碰选择。

选择匹配采用真实 ListView 与 ObservableCollection 的 4000 个已有条目 + 同一 UI 回合逐首追加 400 条；当前歌曲不在投影中，迫使每次匹配扫完整列表。适配器只在 Music.Id getter 记录次数。失败基线读取 **5,040,600 次**，修复后 **0 次即时读取、排队后总共 13,200 次**，对应一次遍历、每项三次 Id 读取；验证了新增条目被正确选中、真实播放变化立即选中、卸载/重载不采用旧状态。

一次最终 PMV2 样本记录追加发布阶段约 **2.4772 ms、1,431,392 字节**，失败基线约 **515.481 ms、136,509,776 字节**。这是固定夹具中 `GC.GetAllocatedBytesForCurrentThread` 对追加同步阶段的采样，**不包含排队匹配阶段、其他线程或真实导入 I/O**；运行间时间有波动，不换算整个应用的分配或速度改善百分比，不宣称零分配。完整原始样本为 `artifacts/song-details/p2-selection-before.json`、`p2-list-dpi-batch.json`。

### 最终验证与独立审查

- 主项目 x64 Debug 最终构建 **0 错误、381 条警告**：`artifacts/song-details/p2-resize-main-final.log`。没有将构建通过替代运行时验证。
- 最终首页/详情原生专项 **416 项、22 次定向 lvt 调用、三轮诊断 GC、前台违规 0**：`artifacts/song-details/p2-resize-home-complete.log`、`artifacts/home-polish-batch2/p2-resize-home-complete/result.json`。包含 24 组几何、12 张真实加载图片的居中、缺图及下一张动画、双向封面/标题连接、编辑器样式、后台读取/取消与迟到发布、悬浮末段像素和离页回收。并非历史上另有旧断言失败的184项套件。
- 完整列表/位置/跨链导航回归 **171 项、15 次 lvt 调用、前台违规 0**：`artifacts/song-details/p2-list-dpi-final.log` 与 `p2-list-dpi-final.json`。两个新 P2 入口均使用真实主导航调用链和原生集合通知。
- 最终真实 **150% DPI、1806 次连续缩放、1807 次尺寸回调、UI 心跳正常、失败 0、前台违规 0**：`artifacts/song-details/resize-dpi-complete.log`、`artifacts/home-polish-batch2/resize-dpi-complete/result.json`、`resize-layout-history.json`。覆盖原失败点、系统尺寸循环消息、正反向密集宽度、自动/紧凑导航布局、分数边距/布局舍入、自动轮播含缺图、长标题和横竖封面。该次数不是1806个硬件/DPI组合。
- 三轮回收后，31个卸载首页中未被首轮UIA观察器持有的30个均回收，最终 AlivePageIndices=[0]；退休详情 panel 和移出封面检查均通过。没有由短期进程/显存计数声明净节省比例。
- PMV2 刚启用但尚未适配夹具初始物理尺寸时，预览测试因为逻辑视口低于产品420 DIP门槛而超时（`p2-resize-home-verified`）；最终夹具按DPI换算原有1120×900 DIP测试窗口后再运行416项通过。产品窗口与门槛未改，没有计入该失败为产品回归。
- 23个修改/新增文件统一CRLF，3个修改XML解析通过；修改C#没有新增字面GetString调用，七语言资源静态检查无缺失，`git diff --check`通过。

独立子代理 `/root/review_today` 对上述三个 P2 修复及布局修复只读复核，核对失败 JSON、真实调用链、资源所有权和测量输入边界，未发现新的确定问题。未编辑代码、构建或操作窗口。主图需要父级提供有限水平测量宽度；当前唯一生产入口 HomePage 关闭水平滚动，满足该约束。若未来放入横向 StackPanel/ScrollViewer，需要另行定义无限宽时的固有宽度。

所有原生测试窗口离屏、Show(false)、禁止激活，lvt 通过指定 HWND 的 UIA Invoke 驱动，没有发送全局鼠标或键盘，也没有调整或重启生产窗口。夹具替代真实音频、网络和大曲库 I/O；导航测试链接实际 MainPage 导航 partial、LibraryNavigationService、浏览页/子 Frame 与 Presenter，替代无关主壳和编辑业务；另一个夹具验证真实详情编辑、模板、SQLite、动画与回收。这些边界不能外推为已在生产窗口完整复现所有功能。

本轮未运行真实用户文件、Release/AOT、所有 GPU/主题或跨屏真实手动拖拽；确认的高 DPI 压力场景为实际 150%，不声称已验证 125%/200% 的每个布局。未修改自编译 CsWinRT、全局异常吞并策略、播放器/IPC 或用户缓存/数据库；不从 GC 可达性推断显存立即释放或进程净内存下降。

## 2026-10-31 记录：16:9 布局与独立当日审查

用户后续明确要求调整比例，本节替代下文旧记录中“维持原主图比例”的描述。当前主图与右侧卡片都采用 16:9，保留上方一张大卡、下方两张小卡，封面沿用 UniformToFill 和水平/垂直居中，裁切溢出内容而不拉伸图片。

### 实现与实际验证

- 通过主图与拼贴等高的约束计算列权重，计入上下和左右的 15 DIP 间距，右侧 1、2、3 张卡片分别保持可见卡片的 16:9；无拼贴时主图跨满两列。保持 Star 测量，避免固定列宽把列间距重复计入期望宽度。
- 主图标题至多两行，说明至多两行；较矮布局隐藏说明，小卡按高度收紧标题字号、边距和行数，过小的图块不绘制放不下的标题，歌曲名仍保留于宿主无障碍名称。没有创建新的封面实例、图片源或解码尺寸设置。
- 实现过程中原生测试测出一次 LayoutCycleException：无拼贴时固定宽列与跨列间距构成尺寸反馈。改回 Star 权重后，以同样的 0–3 张卡片矩阵验证通过；未将失败运行计作通过。
- 定向矩阵覆盖 320、600、900、1280、1820、2460 DIP 六个宽度和 0–3 张右侧卡片，共 24 种组合，检查实际尺寸、16:9、整组底边、外伸箭头及命中。另捕获 1280×720、1920×1080、2560×1440 三种离屏窗口的真实合成帧，检查所有四张已加载图片的实际居中和 UniformToFill。
- 主项目 x64 Debug 构建 0 错误、517 警告：`artifacts/song-details/carousel-16x9-main.log`。
- 整体原生/lvt 专项 **405 项、21 次定向 UIA Invoke、三轮诊断 GC、前台违规 0**：`artifacts/song-details/carousel-16x9-final.log` 与 `artifacts/home-polish-batch2/carousel-16x9-final/result.json`。覆盖此前的预览、末段像素、连接动画、详情状态及资源回收回归。
- 对截图再补强“四个源必须全部加载，不能跳过缺图主封面”后，定向 **288 项**及一轮 GC 检查共 **291 项**通过：`artifacts/song-details/carousel-16x9-centered.log`、`artifacts/home-polish-batch2/carousel-16x9-centered/result.json`。整体 405 项是在补强该测试前运行，不能将两个计数相加宣称为另一套全量测试。
- 最新尺寸数据与截图位于 `artifacts/home-polish-batch2/carousel-16x9-centered/carousel-16x9-layout.json`、`carousel-16x9-1280.png`、`carousel-16x9-1920.png`、`carousel-16x9-2560.png`。截图使用隔离的合成封面；没有操作生产窗口，也没有测量实际大曲库、HDR、高对比度、所有 DPI 或 Release/AOT。回收检查证明受测对象可达性收尾，不代表整个进程内存或显存即时下降。

### 独立子代理审查结果

轮播实现及定向验证完成后，使用未继承父代理对话的独立子代理 `/root/review_today` 进行只读审查。范围为基线 `3434eca674ec85d3e26a12cf2a4cf4ca8d86928b` 至 HEAD 的六个当日提交（`dee9a9fa`、`eaea578c`、`06ff2a36`、`3536b9dc`、`c220b108`、`a1456ba2`）和本轮工作区改动。覆盖功能、并发与生命周期、资源所有权和释放、内存开销、UI 线程与性能；检查工具测试和报告的实际边界。未确认本轮 16:9 生产修改的新缺陷。以下三项是当时发现问题的历史记录；随后已按用户授权修复，最新证据见本报告顶部。

1. **P2：页内详情未随底栏的内层跨链导航关闭，返回状态被旧快照覆盖。** 引入 `3536b9dc`。从歌曲列表打开详情，再点底栏的专辑/歌手；`LibraryNavigationService.SelectAlbum`（37行）调用 `MainPage.NavigateToMusicBrowsePage`（323行），由于主 Frame 已是 MusicBrowsePage，绕过会 Dismiss 的 NavigateTo，仅下层 ContentFrame 导航。Presenter 只订阅主 Frame.Navigating（MusicDetailsPresenter 42行），所以详情仍遮住目标页。之后 `MainPage.CloseMusicDetails`（445行）恢复原歌曲页的 false，覆盖新分组详情的返回可用状态。建议统一跨链导航先关闭临时详情并根据当前来源计算返回状态，补上真实 MainPage/子 Frame/底栏命令集成测试。
2. **P2：旧歌曲属性窗口的冷缓存封面读取回到 UI 线程。** 引入 `dee9a9fa`。`View/SubView/MusicDetailsWindow.xaml.cs:179` 直接把 `ToolUtils.GetRawImage` 交给 `WindowArtworkSession.LoadAsync`（30行），而该方法直接在当前线程调用委托。`ApplicationTasks.RunAsync` 的 Task.Yield 不负责切到后台，仍恢复到 UI；缓存缺失或过期时，`ToolUtils.cs:435/447` 同步解析音频封面或创建 ATL Track。本地大文件、慢磁盘/映射盘会占用 UI，取消不能中断这些同步调用。建议仅将原始字节读取放回后台，BitmapImage 创建/发布继续在 UI，复用现有 MusicDetailsEditorFactory 的读取边界。未测具体阻塞时长。
3. **P2：逐首导入会重复执行全列表播放选择匹配。** 引入 `3536b9dc`。`PlaybackSelectionTracker.cs:58` 对每次 VectorChanged 立即 Synchronize，79行遍历 Items；`AppViewModel.AppendSongsBatch`（460–472行）为保持 WinUI 虚拟化位置，逐首 ListSongs.Add。歌曲页可见且有待播或已播歌曲时，当前播放不在过滤结果中必须每次扫完 N 条；位于列表末部也接近全量。B 首批次成为 O(BN) 的 UI 线程工作，持续导入最坏二次增长并重复创建 WinRT 集合枚举工作。建议合并一次 Dispatcher 同步或按插入索引/变更类型增量匹配，增加大列表连续追加验证。未测实际卡顿、分配字节数或收益比例。

三项由独立代理完成 git/blame 与真实调用链核对，父代理复核关键入口和行号；属于静态调用链证明，**没有在用户生产 UI 中复现**。现有 Universal 夹具替代了 MainPage 导航，窗口封面测试替换了字节读取委托，选择测试未覆盖逐首导入增长，所以这些测试通过不能排除上述问题。

审查没有发现能够确证的新增资源泄漏；这一结论限于所读调用链及现有对象回收证据，不等于证明不存在泄漏。来源 UI 在详情期间存活、Frame 缓存禁用、播放选择不自动滚动均是明确设计，不作为缺陷。独立代理核对修改文件中的 30 个字面 GetString 键，七语言均存在；CRLF 检查通过。真实音频、网络、文件选择器、高对比度、长期运行及 Release/AOT 仍未独立执行。

## 2026-10-31 补充：退出过程明暗脉冲与定位禁用命中

用户确认上轮处理后悬浮退出仍会闪烁。本轮复现表明，上轮的“关闭后 Visibility/Opacity”断言没有覆盖动画结束前的封面颜色变化；保留该检查作为清理验证，新增真实合成帧回归，不能再以关闭后透明作为无闪烁的证据。

### 悬浮封面：单变量对照与修复

在不激活、离屏显示的独立 WinUI 窗口，用 PrintWindow 捕获真实合成内容，保存退出全过程及关闭后四帧；未使用 RTB 代表合成动画。夹具仅通过 [AnimationController](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.animationcontroller?view=windows-app-sdk-1.8) 把当前退出动画实例的 PlaybackRate 设为 0.2，以放慢取样；产品的 200ms 时长和曲线没有改动。

- 保持原实现：末段封面颜色先偏亮再偏暗，最终还原；原生图片布局确认缩略图 160×160，预览封面与 Image 都是 358×358，没有出现大图未填满或自然像素尺寸未缩放。
- 只移除阴影的诊断对照：对应末段像素与原实现相同，未消除明暗脉冲。
- 只取消整层 Opacity 淡出的诊断对照：保留缩放、位移、裁切后，封面中部末段相邻帧平均 RGB 变化从数个色阶降至约 0.21/255。诊断用临时开关已从夹具移除，原始帧仍在 artifacts。

由此锁定的是**整层透明度交接**，不是动画结束后的最终状态。该层包含实心背景并覆盖底部缩略图，退出时同时混合这些重叠内容会产生可见明暗变化。最终移除 _fade 动画及其资源，让封面保持不透明地缩回；正文沿原来的 BottomInset 裁切收起，完成后 Hide 保持透明终态。展开外观、阴影、布局、位移和缩放终点保持原样。

新增断言统计封面中部、最后四张关闭前帧及第一张关闭后帧的相邻变化，阈值为平均 RGB 1.5/255。本次失败基线峰值 **5.285/255**，产品修复后最终专项峰值 **0.309/255**；关闭交接帧为 **0.179/255**，之后三帧变化均为 0。这些数值用于固定场景的视觉回归，不是性能收益或所有图片、设备的通用保证。

证据：

- `artifacts/song-details/hover-pixels-before.log` 明确失败于像素脉冲断言；`artifacts/home-polish-batch2/hover-pixels-before/hover-frames.json` 与逐帧 PNG。
- 阴影对照：`artifacts/home-polish-batch2/hover-no-shadow-before/`；只去掉淡出的对照：`artifacts/home-polish-batch2/hover-opaque-before/`。
- 最终：`artifacts/home-polish-batch2/hover-flash-fixed/hover-frames.json`、`hover-frame-*.png`、`hover-layout.json` 与 `result.json`；日志 `artifacts/song-details/hover-flash-fixed.log`。

### 禁用定位按钮：命中链与修复

失败基线在实际歌曲页把播放歌曲设为空，保留真正的禁用按钮，在其中心点调用原生命中测试（includeAllElements=false）。第一命中是下层 `ListViewItemPresenter`，证据为 `artifacts/song-details/locate-hit-before.log` 与 `locate-hit-before.json`。

六个入口现在都有独立的启用 Border 命中面，尺寸 44×44、外边距 16、圆角 22、背景 Transparent，与列表同级且在其上方；按钮放在里面并把本地 Margin 设为 0，保留原按钮的 IsEnabled 绑定、模板、命令/Click、焦点、资源与无障碍名称。四个原先位于 Grid.Row=1 的入口把 Row 移到命中面上。禁用子按钮后，命中落在 Border，事件路由不会进入它下面的 ListView；没有用“永远启用按钮、在点击时忽略”替代禁用语义，也没有扩大到周围 16px 留白。

实际歌曲/最爱页的原生定位、位置和交互回归共 **159 项、13 次 lvt Invoke、前台违规 0**；启用后仍能点击实际按钮并居中。日志 `artifacts/song-details/locate-hit-fixed.log`，结果快照 `locate-hit-fixed.json`。分组/歌单详情和两个队列入口通过同一命中面静态核对及主项目构建，本轮没有逐一在生产窗口做鼠标操作。

### 构建、资源与验证范围

- 主项目 x64 Debug：0 错误、517 个警告，`artifacts/song-details/hover-locate-main.log`。
- 首页/详情原生专项：**165 项、21 次 lvt Invoke、三轮诊断 GC、前台违规 0**；包含原有滚动退出、详情双向动画和对象回收检查。没有把该专项混称为此前存在旧失败的 184 项全量首页回归。
- 所有修改统一 CRLF；XML 与七语言资源静态检查，没有新增 GetString 键、改播放器、操作用户缓存或数据库。
- 实际合成帧检查采用夹具固定封面、Light 主题、当前 DPI 和本机 GPU，并有意放慢当前动画实例；未对所有主题、DPI、真实图片或生产窗口进行逐帧录屏。按钮穿透用原生命中链验证，lvt 使用 UIA Invoke 驱动测试操作，没有向前台发送物理鼠标/滚轮输入。

## 2026-10-31 追加：封面生命周期与双向交接时序

本轮使用 diagnose、winui-design、lvt 技能，读取用户指定的 `C:\Users\90684\Documents\OriginalSoundPlayer\Logs\WinUIMusicPlayer-20261031.log`，在现有独立原生夹具中复现后修改生产实现。没有启动或操作用户正在使用的生产窗口，没有更改 Carousel 的尺寸、位置、圆角或材质。

### 已确认的问题与修复

1. **缩略图与圆角一起消失**：真实原生加载循环出现 `Loaded → Loaded → Unloaded`，最后一个回调到达时 `IsLoaded`、内部内容的 `IsLoaded` 均为 true。旧卸载回调清掉了新图片、加载令牌和合成 Clip；这直接解释占位符与方角同时出现。现在只有确认已经离树才清理，旗标未更新时排队复核，重新挂入的控件保留新资源。旧事件记录位于 `artifacts/home-polish-batch2/hover-cycle-repro/cover-cycle-events.json`，修复后同类记录位于 `artwork-detail-final/cover-cycle-events.json`。
2. **取消与损坏缓存**：用户日志存在 150px 缓存过小及取消解码错误，而悬浮面板的较大图片仍能加载。基线直接运行实际 WIC 解码方法，已取消令牌仍被吞成成功，并留下 54 字节 BMP 文件。现在取消继续抛出；缓存先写同目录独立临时文件，写完、刷新、关闭后再替换最终文件，失败清理临时文件。缓存读者允许文件被替换，原有缓存格式不变。没有清空用户缓存，旧损坏文件仍由既有校验逻辑按需重建。
3. **悬浮面板退出末帧闪烁**：原先动画结束后先 Restore 到全尺寸、不透明，再 Collapsed；基线观测到关闭后的合成 Opacity 为 1。现在 Hide 保持透明终态，下次 Reveal 才复位。滚动回调原先直接 Dismiss；改走原来的 200ms CollapseToCover，保留图片到实际合成动画完成，重复滚动不重启动画。离页和尺寸变化仍及时清理。
4. **详情切换过早隐藏与样式闪烁**：基线标题点击的同一调用内来源 Opacity 已经为 0，来源根 IsEnabled 为 false，标签 TextBox IsEnabled 也为 false；连接动画却要等之后的排队回调才 TryStart。现在让目标以近透明状态完成布局并经过两次实际 Rendering 回调，再在同一 UI 回调 Prepare/TryStart 封面与标题，随后交接内容透明度。返回同样预先恢复来源渲染。内容用 150ms 透明度交接，没有平移、缩放整页或 Frame Entrance。
5. **交互与清理**：来源不再禁用整树，只禁止命中；详情聚焦标题并使用 Cycle 限制 Tab 范围。九个标签 TextBox 加载中只读，加载完成后可编辑，远程歌曲继续只读。渲染等待由 presenter 持有，返回、请求替换、离页和 Dispose 均解绑；准备中返回不显示详情，进入中返回直接反向。仍保留同一 Frame、Page 和 ListView，详情没有全局单例或导航缓存。

### 本轮证据

- 失败基线：`artifacts/song-details/hover-artwork-before.log`、`hover-cycle-repro.log`、`detail-styles-before.log`。最初用程序 ChangeView 触发滚动的用例没有触发 ViewChanging，超时不能算作产品故障证据；最终用例调用实际 XAML 绑定的滚动处理器，并观察真实原生合成退出。
- 最终主项目 x64 Debug：0 错误、381 个警告，`artifacts/song-details/artwork-detail-main-final.log`。独立夹具 0 错误、29 个警告；没有改动播放器或 IPC，无需重新发布 AudioPlayer。
- 最终原生/lvt：**163 项检查、20 次定向 UIA Invoke、三轮诊断 GC、前台违规 0**。`artifacts/home-polish-batch2/artwork-detail-final/result.json` 与 `artifacts/song-details/artwork-detail-final.log`。复跑：`./_tools/HomeUiRegression/Run-Polish.ps1 -RunName artwork-detail-final`。
- 封面检查涵盖反复移出/挂入后图片与 Clip 保留、预取消和解码启动后取消、完整缓存输出、目标文件被锁时保留旧缓存且无临时文件残留。锁文件测试故意触发一次写缓存 IOException，属于故障注入，不能解读为真实用户缓存写入失败。
- 详情检查涵盖真正的封面与标题正反向 Completed、TextBox 原生模板已经实现且加载中保持启用/只读、来源根 IsEnabled 变化为 0、相同来源实例/行/滚动偏移/选择、真实 SQLite 保存与迟到读取隔离。另验证渲染前返回、连续替换准备请求、进入动画中返回、准备期间卸载；实际 Rendering 记录在 `artwork-detail-final/detail-render-timeline.json`。
- 三轮 GC 均确认退休的 **7 个详情 panel**、循环重载后移出的封面可回收。31 个首页中保留第一个 UIA 观察对象，其他 30 个在最后一轮后均不可达；这沿用夹具的既有观察者隔离规则，不声称 31 个全部回收，也不由此推导显存即时下降。

后台测试隔离实际音频、网络和用户文件；封面解码、控件、模板、渲染回调、合成动画、SQLite 与 GC 是真实实现。为避免影响前台，没有发送真实鼠标滚轮，也没有在生产窗口逐帧录屏。RTB 静态输出与旧基线均存在文本编辑器内容缺失，没有把它当作动画末帧像素验收。因此，这里证明了导致闪烁的状态切换与错误交接时序被移除，不宣称所有 DPI、主题和 GPU 上已经逐帧验证。既有 184 项全量首页旧断言的限制仍见下文，不将本轮 163 项专项混称为该全量通过。

## 最终承载方式

详情是当前页面中的 `MusicDetailsPanel`（UserControl），复用通用 `MusicDetailControl` 和独立 `MusicDetailsViewModel`。歌曲列表、最爱、分组与歌单中的歌曲标题进入同一详情流程，两个播放队列沿用同一展示入口。

来源页从创建时就拥有来源内容与详情两个 presenter。打开详情先准备目标渲染，再交接来源的可见性与交互；来源不切换 IsEnabled，避免整个控件树的 Disabled/Normal 视觉状态过渡；来源仍在可视化树中，既不 Collapsed、不 Unloaded，也不重设 ItemsSource。返回只收起临时详情层，因此保留同一 Page、ListView、已实现的歌曲行控件、滚动位置和选择。

`MusicDetailsPresenter` 不调用 Frame.Navigate/GoBack，不更换 MainFrame.Content，不修改 BackStack，也不再包含主/内层 Frame 的额外过渡抑制逻辑。普通页签和侧栏导航保持原有行为。这里的保留仅持续到关闭详情或离开当前页面；没有为其他已离开的页面恢复缓存，也没有注册全局详情视图单例。

## 连接动画与生命周期

- 列表左侧封面和歌曲标题分别连接到详情的封面和标题；进入使用 Gravity，返回使用 Direct。两组动画均完成或安全取消后，才移除详情并释放其图片绑定。没有叠加整页进入动画。
- 通过弱引用和歌曲标识解析返回目标，必要时查找仍已实现、仍可见的对应行。数据更新导致目标消失时取消相应连接，不强行滚动或连到回收后代表另一首歌的行。
- 隐藏来源时停止该行的悬停文字滚动；首页作为来源时暂停轮播和预览。关闭恢复来源活动。仅真正从该行键盘进入、且按钮完全可见时才恢复键盘焦点，防止 Focus 引发隐式 BringIntoView。
- 离开整个来源页、覆盖请求或目标卸载会取消快照和迟到回调。编辑器开始关闭时取消可取消读取；退出屏障继续等待真实在途任务与已接受写入。没有加入生产 GC.Collect。
- 详情播放使用捕获的来源类型，队列入口保持现有队列并传递具体条目标识；标签和歌词继续使用既有草稿与 SQLite 保存管线。

连接配置依据微软的 [Connected animation 指南](https://learn.microsoft.com/en-us/windows/apps/develop/motion/connected-animation)。通用详情如今完全不发起 Frame 导航，因此无需修改其进入/返回过渡。

## Carousel：修复裁切而保持外观

上一版把按钮移到封面内部，改变了原外观，已撤销。

最终保留按钮相对主图原有的位置：左边界 -10 DIP，右边界在主图右侧再伸出 10 DIP。封面、缩略拼贴、分页点的尺寸及页面坐标、按钮材质与圆角均沿用原定义。

轮播为外伸控件保留 16 DIP 的绘制空间，用等量布局补偿保持图片坐标；箭头移到独立覆盖层，避免被主图内容层裁切。首页横向 24 DIP 留白改为等量 padding，使外伸部分仍处于父内容的实际绘制边界内。

原生回归分别检查 320、600、900 DIP、有/无拼贴六种组合。每次既验证图片原位置与宽度、箭头原有外伸坐标，又在左右两个按钮的左/中/右取样，共 36 个点用原生命中检测确认没有被边界裁掉。它验证的是外观几何保持和完整命中区域，不能代替所有 GPU、DPI、主题下的逐帧像素对照。

## 数值输入与播放选择

11 处 Compact NumberBox 改用 Inline；歌词偏移宽度 176、缓存上限宽度 160，保留既有范围、步长和双向绑定。页内详情已验证数值写回草稿；旧属性窗口、曲线和设置面板的高 DPI/多语言尺寸矩阵未操作验证。

播放选择与滚动独立：跟随待播/确认状态选择当前显示集合中的对应项，取消待播后恢复确认条目。普通列表按引用、标识或外部路径匹配，歌单支持包装项，队列区分重复条目。所有外部订阅只在视图加载期间存在。

## 验证证据与范围

- 主项目 x64 Debug：构建 0 错误、381 个警告，日志 `artifacts/song-details/in-page-main-verified.log`。
- 列表原生/lvt：157 项通过，13 次按钮调用，前台违规 0；保留播放选择、无自动滚动、手动居中及五页浏览位置回归。删除了为“返回重建”编写的两项旧检查。
- 页内详情定向测试：真实列表滚到第 21 行并选中该行，打开、返回、保存后验证相同来源 Page 和行对象、相同偏移与选择；Frame 仅在夹具初始化时导航一次，详情产生 0 次导航和 0 条历史。
- 观察到封面、标题正向及反向四个真实 ConnectedAnimation.Completed；返回配置为 Direct。使用实际通用控件、编辑 ViewModel、歌词仓库和 SQLite 验证保存、独立会话、迟到封面隔离及离页后接受写入的归属。
- 原生首页/详情/轮播专项 119 项通过，15 次定向 lvt 调用，三轮诊断 GC：`artifacts/home-polish-batch2/in-page-verified/result.json`，文本日志 `artifacts/song-details/in-page-verified.log`。包含三轮诊断 GC，退休详情控件均可回收；前台违规 0。
- 修复前 `artifacts/song-details/inline-before.log` 失败于“必须保留同一来源页”，锁定了原先替换 Frame 内容的错误承载方式。最终测试增加了真实滚动偏移、选择和四个方向完成事件的检查。
- 修改文件统一 CRLF；静态校验 XML 和所有语言中的 GetString 资源键。构建不作为运行资源解析的替代证据。

独立夹具用记录参数的音频适配器代替实际播放引擎，封面 I/O 使用受控异步关卡；没有激活、点击用户的生产窗口，也没有测试真实网络/音频设备。本轮未重新宣称既有 184 项首页全量回归通过：之前当前代码与提交前生产代码对照都出现过悬浮重入及 ScrollTo 172 的旧断言失败，记录仍在 `artifacts/song-details/home-full-result.txt`、`home-baseline-result.txt` 和 `home-baseline-scroll-result.txt`。

保持当前来源 UI 是无重建返回的必要成本。它不等于缓存所有页面；关闭详情后释放详情对象，离开来源页后仍走原来的禁用缓存与卸载清理。GC 结果证明对象可达性收尾，不代表整个进程 Private Bytes 或显存即时下降，本轮不宣称具体净内存节省比例。
