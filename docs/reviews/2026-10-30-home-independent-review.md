# 2026-10-30 首页改动独立审查

结论：发现 2 项 P2 问题，建议修复后合入。未确认 P0/P1；通过现有构建和回归不能覆盖真实封面工厂的取消与空结果契约。此报告仅审查，不修改生产代码、测试或配置。

## 固定范围与方法

- 时区与日期：Asia/Tokyo，2026-10-30。
- 基点：`2e75e928b240d575122df5907cf18c81027a9c46`；目标：`936c15a7d0c3ebb185e1e43784fc85ce525ff5ac`。
- 提交：`9c9b3f09`、`cd587cd2`、`6672cb77`、`936c15a7`；43 个变更文件，3895 行新增、330 行删除。
- 依照 `AGENTS.md` 与 `code-review-excellence/SKILL.md`，阅读 C#、架构、性能和通用质量参考；内存部分另采用 `winui3-gc-stw-analysis/SKILL.md` 的代码审查口径，区分分配、存活保留、原生/GPU 资源与工作集。仅使用一个独立审查子代理，无下级代理。
- 逐项检查差异，并沿首页导航、属性草稿、歌词事务、真实封面 API、退出屏障和合成资源调用链核对；主会话执行构建/回归，独立审查读取当前日志，不把文档中的历史通过当作本次证据。

## Findings（按严重性）

### 1. [P2] 封面工厂遗漏取消令牌，离页后继续请求并延长退出等待

- 定位：`Services/MusicDetailsEditorFactory.cs:33`、`:39`。
- 触发：打开需从 WebDAV 或较慢文件读取封面的歌曲属性，或者执行“在线匹配封面”，在请求未完成时导航返回或退出应用；慢速、离线或无响应服务更容易观察到。
- 证据：`ReadCoverAsync` 仅把 `token` 传给 `Task.Run`，其委托调用 `ToolUtils.GetRawImage(music, true)` 时使用默认令牌；工作已经开始后，取消 `Task.Run` 的令牌不会取消内部异步 I/O。`FindCoverAsync` 同样调用 `GetMixedCoverImageAsync(music)`，只在返回后检查取消。
- 完整链路：`HomePage.CollapseDetail` → `HomeViewModel.BeginCloseDetails` → `MusicDetailsEditorViewModel.BeginClose`（`:228` 取消 `_closed`）→ 工厂忽略该令牌。真实 `ToolUtils.GetRawImage`（`Utils/ToolUtils.cs:401`）本已把令牌传递到 WebDAV 读取和缓存文件读取（`:409-410`、`:424`）；初始调用的 `isManual=true` 不走本地自动网络回退（`:939`），这里的网络取消问题由 WebDAV 与显式匹配路径成立。真实 `LrcService.GetMixedCoverImageAsync`（`WebService/LrcService.cs:45`）也会传到搜索与 `HttpClient.GetByteArrayAsync`（`:53-56`、`:78`、`:206`）。
- 影响：已离开的编辑会话仍继续文件/网络工作，并被 `StopAsync` 的真实任务等待保留（`MusicDetailsEditorViewModel.cs:242`）；快速反复进入可增加同时收尾的请求与临时会话。显式在线匹配路径在退出时会经 `EditorSessions.StopAsync` / `HomeViewModel.StopAsync` 等待网络自然完成；WebDAV 另有服务级 `_stop`（`Services/WebDavLibraryService.cs:330`），退出时远程服务停止能最终取消它，但正常导航不会停止该服务。这里确认的是页内无效工作与延迟释放，未据此宣称永久内存泄漏或固定延迟。
- 复现步骤：用响应可控的远程封面服务保持请求未完成；打开属性或在线匹配后返回，检查请求取消状态与对应 `StopAsync`；当前请求仍保持运行，直至服务结束或自身超时。
- 修复方向：分别传入 `ToolUtils.GetRawImage(music, true, token)` 与 `GetMixedCoverImageAsync(music, token)`；保留对真正不可取消边界的完成等待，不能用取消 await 代替工作收尾。增加覆盖真实工厂边界的取消验证。
- 归类：本次新增首页工厂路径的问题；旧属性窗口也有部分省略令牌的调用，本结论不将其扩大为本次新改动。现有 Home UI 使用 `FixtureEditorFactory`，不执行此真实工厂，因此其通过不否定该问题。

### 2. [P2] 正常无封面返回空数组，被误报为属性加载失败

- 定位：`ViewModel/Controls/MusicDetailsEditorViewModel.cs:147`、`:150`。
- 触发：歌曲没有内嵌封面、缓存封面或可匹配网络封面；例如专辑未知且无图的正常本地音频。
- 证据：`ApplyCoverAsync` 只将 `null` 当作无图；真实 `ToolUtils.GetRawImage` 在找不到图片时返回 `[]`（`Utils/ToolUtils.cs:453-455`、`:490`，异常回退也是 `:498` / `:503`）。真实 `ConvertByteArrayToBitmapImage` 明确对零长度数组返回 `null`（`:371-372`），随后这里抛出 `FailedObtainCover`。
- 完整链路：`Start` → `LoadCoreAsync` 先成功加载歌词和标签 → `_host.ReadCoverAsync` → `ApplyCoverAsync([])` → 抛错 → `LoadCoreAsync` 的 catch 设置 `LoadFailed=true`（`:142`）和 `Error` → `HomeMusicDetailControl.xaml:366-368` 显示加载失败/重试。再次重试仍然把相同的正常空结果当失败。
- 影响：合法的无图曲目进入属性就出现错误与无效重试入口；原有占位封面本可正常呈现。它不会必然阻止数据库保存，但会把成功加载的属性误标为失败。
- 复现步骤：导入一首确认不含图片、专辑未知的本地音频，从首页进入属性；歌词和标签已可用，仍显示“获取封面失败”及重试，重试不能改变无图事实。
- 修复方向：初始读取将 `null` 与 `Length == 0` 统一为正常无图，保留占位；非空字节无法解码时再报错。显式在线匹配无结果的提示可继续由 `ChangeCoverCoreAsync` 单独处理。
- 归类：本次新增编辑器的空状态回归。旧 `MusicDetailsWindow` 允许转换结果为 `null`，没有这一抛错。UI 夹具的 `ToolUtils.GetRawImage` 对缺图返回 `null`（`_tools/HomeUiRegression/Adapters.cs:102`），与生产返回空数组不同，掩盖了该边界。

## 43 文件覆盖清单

`reviewed` 表示已阅读全部变更，并按表中范围核对；没有默默漏看的变更文件。资源翻译只做结构与明显文字检查，不替代各语言人工语言质量验收。文档中的外部 Store/XBF 研究材料未重做。

| 类别 | 变更文件（个数） | 状态与检查范围 |
| --- | --- | --- |
| 依赖注册 | `App.xaml.cs`（1） | reviewed；注册工厂，沿启动、显式 Start、EditorSessions 与 ShutdownCoordinator 核对拥有者和停止顺序。 |
| 编辑宿主边界 | `Services/IMusicDetailsEditorFactory.cs`；`Services/MusicDetailsEditorFactory.cs`（2） | reviewed；创建时机、延迟 DI、封面读取/匹配/选择/导出、通知边界；发现问题 1。 |
| 推荐内核 | `Services/Recommendations/HomeRecommendationService.cs`（1） | reviewed；每栏 12→20、探索插槽、候选上限与小库放宽，读取完整选择算法及回归。 |
| 本地化 | `Strings/de/Resources.resw`；`Strings/en/Resources.resw`；`Strings/es/Resources.resw`；`Strings/ja/Resources.resw`；`Strings/ru/Resources.resw`；`Strings/tr/Resources.resw`；`Strings/zh-CN/Resources.resw`（7） | reviewed；全部新增资源差异/XML、七语言键集合、x:Uid 与新增 GetString 对应。各新增 41 键，无新增重复键或缺失 GetString 键；未做七语言逐屏人工验收。 |
| 操作样式 | `Style/BtnStyle.xaml`（1） | reviewed；模板、交互状态、共享按钮与动画入口，结合原生 UI 断言。 |
| 合成投影与倒计时 | `View/Controls/CarouselArtworkShadow.cs`；`View/Controls/CarouselCountdown.cs`（2） | reviewed；surface/brush/effect/mask 所有权、分项释放、订阅解绑、动画复用/停止、依赖动画回调。 |
| 轮播与照明 | `View/Controls/CarouselLighting.cs`；`View/Controls/FeaturedCarousel.xaml`；`View/Controls/FeaturedCarousel.xaml.cs`（3） | reviewed；共享光源、表达式/关键帧、卸载释放、列表缩短、自动播放暂停续跑、箭头淡出与键盘焦点、布局比例。 |
| 拼贴与主图 | `View/Controls/HomeCarouselTile.xaml`；`View/Controls/HomeCarouselTile.xaml.cs`；`View/Controls/HomeHeroCard.xaml`；`View/Controls/HomeHeroCard.xaml.cs`（4） | reviewed；封面/文字的空源和替换、布局缩放、色调与事件解绑、主图动画复位。 |
| 属性视图 | `View/Controls/HomeMusicDetailControl.xaml`；`View/Controls/HomeMusicDetailControl.xaml.cs`（2） | reviewed；双向草稿绑定、远程只读、三个工作区、菜单、保存确认、错误与忙状态、窄窗 ribbon、矮窗侧栏、Pivot 视口尺寸约束和 footer。最小窗口/七语言/高 DPI 连续尺寸矩阵未实测。 |
| 卡片与悬浮层 | `View/Controls/HomeSongItem.cs`；`View/Controls/HomeSongPreview.xaml`；`View/Controls/HomeSongPreview.xaml.cs`（3） | reviewed；普通卡片 Invoke/Enter/空格、UIA 投影、共享命令、合成入退场、重入取消完成事件、菜单保留、卸载释放。 |
| 封面订阅 | `View/Controls/MusicCoverControl.xaml.cs`（1） | reviewed；后台 ImageHash 通知切回 Dispatcher，读取未变更的加载/取消/图片拥有权实现核对迟到回调。 |
| 栏目分页 | `View/Controls/RecommendationShelf.xaml`；`View/Controls/RecommendationShelf.xaml.cs`（2） | reviewed；ScrollView/ItemsRepeater、禁用滚轮与平移、目标累加与完成关联 ID、外部滚动后基点、按钮边界。真实鼠标/触摸板输入仍需设备验收。 |
| 首页与全局返回 | `View/HomePage.xaml`；`View/HomePage.xaml.cs`；`View/MainPage.xaml.cs`（3） | reviewed；真实输入转发、浏览禁用、Connected Animation 双向目标与取消、弱源引用、焦点/BringIntoView、预览位置、离页绑定停止与重入、主导航返回优先级。 |
| 编辑与首页状态 | `ViewModel/Controls/LyricsEditorViewModel.cs`；`ViewModel/Controls/MusicDetailsEditorViewModel.cs`；`ViewModel/Pages/HomeViewModel.cs`（3） | reviewed；歌词真实任务记录/拒绝重复、标签快照/已提交发布、编辑期间继续输入、取消前后事务、编辑器退役等待、歌单队列捕获、更新失效和 UI 发布；发现问题 2。 |
| 项目配置 | `WinUIMusicPlayer.csproj`（1） | reviewed；新增 Satori SDK 导入及现有 PublishAot/trim/R2R/MSIX 条件；不把 Debug 构建当 NativeAOT 兼容性证据。 |
| 回归程序 | `_tools/HomeRecommendationRegression/Program.cs`；`_tools/HomeUiRegression/Adapters.cs`；`_tools/HomeUiRegression/EditorAdapters.cs`；`_tools/HomeUiRegression/HomeUiRegression.csproj`；`_tools/HomeUiRegression/Program.cs`（5） | reviewed；所有新增断言、桩的异步/线程/SQLite 边界、真实 XAML/Composition 覆盖；明确真实工厂和网络/选择器未执行，空图返回契约与生产不一致。 |
| 变更说明 | `docs/Changelog.md`；`docs/HomeRecommendationsPlan.md`（2） | reviewed；用户可见行为、已声明验证和限制，与目标提交一致性；历史通过与本次重新验证分开记录。 |
| 合计 | 43 | reviewed 43；skipped 0 个变更文件。未执行的端到端/测量边界见后文。 |

## 设计、性能、分配与内存静态结论

- MVVM：规范曲目、Metadata 草稿和 Lyrics 草稿的职责可辨；视图主要转发输入与管理布局/动画。工厂将延迟服务解析集中在打开属性的边界，没有在首页构造时主动创建歌词网络编辑器。
- 保存正确性：保存捕获独立快照；`LastCommittedMetadata` 以引用关联提交事实，`AcceptSaved` 发布已提交字段并检测期间新输入。歌词事务使用 revision，远程禁用文件写入，保存后经同一导航关闭。未确认本次引入的保存覆盖或并发单飞丢弃问题。
- 生命周期：歌词 `WhenIdleAsync` 记录已接受的真实任务，重复守卫不覆盖；`StopAsync` 等待编辑操作及歌词任务。投影释放 visual/brush/factory/gradient stops/surface 并断开 SourceVisual，照明移除 Targets 与事件，预览注销 batch 完成并释放自有 clip/animation，页面卸载停止计时器、Connected Animation 和绑定跟踪。除问题 1 的延迟收尾外，未找到有完整根引用链的永久资源泄漏；未做托管/native/GPU 堆快照，不能宣称没有泄漏。
- 分配：封面原始字节、解码图片和排队文件写入的封面副本仍是独立成本；编辑会话退出后清空 `_cover`/CoverPreview。动画和几何对象复用不等于全链路零分配；倒计时为 dependent animation，Remaining 的几何更新仍在 UI 路径。未测每帧分配、GPU 表面占用、长期 working set 或 GC 停顿。
- 扩展性：每栏最多 20，候选池仍有界，分页维持 ItemsRepeater；刷新单在途并合并请求，UI 快照分批让出，后台推荐计算与 UI 发布分离。新增预览没有逐帧 Task/定时器；滚动、刷新、离页能关闭它。问题 1 会使离开的编辑器继续 I/O，应先修复。
- 可读性：状态与所有者大体明确；不以文件长度、局部微分配或拆分类数量本身提出阻塞建议。本报告不以未测量的理论收益要求 Span/池化重构。

## AOT / trimming 静态结论

- 新 UI 控件和 UIA peer 使用 partial；x:Bind 走静态类型的属性/命令，工厂使用明确的构造与泛型 GetRequiredService，没有新增 Assembly 扫描、Activator 或动态代码生成。回归夹具自身通过反射查字段/类型，只属于诊断程序，不能作为主程序 NativeAOT 行为的替身。
- 已阅读主构建日志，未找到本次 Home/Carousel/Editor 变更对应的 CsWinRT 或 AOT 诊断；仓库仍有既有 CsWinRT1028/1032 等警告，不能据此宣称整个应用兼容 AOT。
- Satori SDK 导入影响发布工具链。本次 Satori NativeAOT 缺精确 AotSdk 包且外部源不可达；命令行临时禁用 Satori 后，标准 NativeAOT 离线还原成功，发布又在 WinUI XAML 元数据阶段失败。两条路径均未进入 ILC、未产出或启动 NativeAOT 程序；这是验证受阻，尚无证据认定为本次源码引入的 AOT 缺陷，也不能判为通过。已恢复原 Satori 常规还原设置，并以 Debug x64 开启 AOT/Trim 分析器重新编译成功（0 错误、1778 警告）；53 条去重 IL/CsWinRT 诊断没有命中今天的变更文件。该静态分析仍不等同真正 NativeAOT 发布。

## 本次运行和静态证据

以下为主会话在当前目标源码上重新执行，独立审查已读取结果；没有在同一 obj 目录并行构建。

| 检查 | 当前结果 | 证据文件 |
| --- | --- | --- |
| 主项目 Debug x64 --no-restore | 成功，0 错误，1609 警告；多数为既有警告，未把警告总数等同新增问题数 | `artifacts/review-2026-10-30/main-debug-build.log` |
| HomeUiRegression 构建与运行 | 0 错误、1101 构建警告；进程退出 0，结果文件 173 行 PASS | `artifacts/review-2026-10-30/home-ui-result.txt` |
| HomeRecommendationRegression | 33 项通过 | `artifacts/review-2026-10-30/home-recommendation-result.log` |
| MusicMetadataRegression | 42 项通过 | `artifacts/review-2026-10-30/music-metadata-result.log` |
| LyricsUiRegression | Debug 构建成功（0 错误、1 警告）；隔离子进程 TEMP/TMP 后完整场景通过，结果为 1 行汇总 PASS | `artifacts/review-2026-10-30/lyrics-ui-isolated-temp-result.txt` |
| Satori NativeAOT 尝试 | --no-restore 被 RID/PublishAot 一致性校验拦住；匹配还原失败 NU1102，缺精确版本 `PublishWithSatoriGC.AotSdk.win-x64=11.0.0-satori.40992540404.1`，外部源网络不可达 | `artifacts/review-2026-10-30/main-aot-publish.log`；`main-aot-restore.log` |
| 标准 NativeAOT 隔离尝试 | 仅命令行 `UseSatoriGC=false`，未改配置；本机标准包离线还原成功，发布停于 WMC1007 WinUI 元数据解析；未执行 ILC | `artifacts/review-2026-10-30/stock-aot-restore.log`；`stock-aot-publish.log` |
| Debug AOT/Trim 分析器编译 | 原 Satori 常规配置；`EnableAotAnalyzer=true` / `EnableTrimAnalyzer=true`，成功，0 错误、1778 警告；53 条唯一 IL/CsWinRT 诊断未命中本次变更文件；仅静态分析 | `artifacts/review-2026-10-30/main-debug-aot-analysis.log`；`aot-analysis-warning-details.txt` |
| 常规还原状态恢复 | 成功，未修改源码或项目配置 | `artifacts/review-2026-10-30/main-regular-restore.log` |
| git diff --check 基点..目标 | 通过 | 独立审查只读执行，退出 0 |
| 43 文件 CRLF | 通过，无孤立 LF | 独立审查逐文件读取字节 |
| 新增 GetString 与七语言资源 | 10 个新增调用字面键均为独立键、七语言均存在；各新增 41 资源键，无新增重复 | 独立审查 XML 与 git diff 脚本 |

Lyrics UI 首次运行在默认临时目录的外部清空场景遇到 AccessDenied；没有修改代码，改用工作区内隔离 TEMP/TMP 后通过。该失败归因于本次执行环境的目录权限，不能计为本次产品回归。

推荐夹具单次合成测量：1 万曲目 80.5 ms / 3.77 MiB，10 万曲目 286.1 ms / 36.44 MiB；输入创建排除在外。这仅测 `HomeRecommendationService.Build` 的本机本次场景，不代表冷启动、真实曲库、界面线程耗时、泄漏或零分配，也不构成相对基点的性能收益结论。

## 既有问题与剩余盲区

- `Strings/zh-CN/Resources.resw` 的 `ArtistSplitSymbols.Text` 重复键已用固定基点确认存在，不列为本次回归。
- 没有使用个人曲库、真实 WebDAV/歌词/封面服务、系统文件选择器和真实音频设备作端到端验收。Home UI 运行了真实 WinUI、编译绑定、Composition 和 SQLite，但 App 状态、网络/封面宿主及部分数据库外层入口是适配边界；这也是两个发现未被现有回归捕获的原因。
- 新布局本次回归覆盖常规及 500px 窄窗；宽度充足的矮窗侧栏、最小高度、长歌词/长路径/长语言标签、不同 DPI 的组合矩阵，以及真实鼠标滚轮/触摸板不跳跃仍需交互验收。
- 未做长期反复开关的托管/native/GPU 堆测量、工作集对比、帧时间或 GC/STW 采样；无测量支持时不下“零分配”“无泄漏”或性能改善百分比结论。


## P2 修复跟进（主会话，2026-10-30）

原报告固定提交范围和独立审查结论保持为历史记录；以下记录随后工作区中的修复与验证，并非新的独立审查。

- P2-1 已修复：真实 `MusicDetailsEditorFactory.EditorHost` 向初始读取和在线匹配透传会话取消令牌，继续等待实际任务完成。
- P2-2 已修复：只在初始加载时将 null/空数组视为无图，不调用解码；非空坏图、显式查找无结果和选择空文件的原有错误语义保留。
- Home UI 回归现已编译生产工厂；下游图像/在线来源仍由可控适配器隔离，新增检查不访问个人曲库或真实网络。适配器无图返回空数组、坏图解码返回 null，与生产契约一致。
- 修复前回归在 `real editor factory accepts empty bytes as a normal missing cover` 失败；修复后 184 项通过，包含 11 项新增空结果/坏图/恢复/保留封面/实际宿主取消与清理等待检查。
- 主项目 x64 Debug + `EnableAotAnalyzer=true` + `EnableTrimAnalyzer=true` 编译成功，0 错误；新增 GetString 字面键与七语言匹配，CRLF 和 `git diff --check` 通过。未重跑完整 NativeAOT 发布，之前的工具链限制仍适用。
- 证据目录：`artifacts/p2-fixes-2026-10-30/`，包含 `home-ui-before-result.txt`、`home-ui-result.txt`、`main-debug-aot-analysis.log` 与 `static-checks.txt`。
