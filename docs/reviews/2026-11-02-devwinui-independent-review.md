# 2026-11-02 DevWinUI 完整移植独立审查

原始审查结论（针对 `dab6627d`）：发现 2 项 P2 功能一致性问题及 1 项 P2 测试时序问题；建议修复后再认定与上游完整等价。未确认 P0/P1 或永久资源泄漏。资源优化优先考虑延迟创建滚动合成资源；四边取色的现有优化有实测收益，应保留。本轮仅输出审查结果。

## 范围与方法

- 待审提交：`dab6627da8ea25325577415f4f566e821ad0e3c8`；父提交：`f736abdf`。开始审查时工作区干净。
- 上游基准：DevWinUI 10.8.0，提交 `28d9f74fc1ad86d6597b6ad43b6122e52a1b49aa`。对本地下载的 23 个上游 C# 源文件计算 Git blob hash，与固定提交的 tree 记录匹配。
- 两个不继承实现对话的独立子代理分别审查功能一致性、资源/性能/分配；主代理核对调用链、复跑已有回归并汇总。采用 `code-review-excellence` 及 `winui3-gc-stw-analysis` 的审查口径，不将工作集、托管分配与原生资源保留混为一谈。
- 只评不改。生产代码、既有测试与配置未修改；复现和测量放在忽略的 `artifacts` 目录，正式产物仅此报告。

## 已确认问题

### 1. [P2] 无关属性更新终止呼吸动画，且无法直接重启

- 定位：`Controls/CompositionShadow.cs:166`、`:171`、`:335`。
- 触发：控件加载后开启 `IsBreathing` 或调用 `StartBreath()`，随后修改 `Color`、`OffsetX/Y/Z` 或 `ShadowOpacity`；图像边缘取色完成后更新 `Color` 也会触发。
- 调用链：全部普通阴影 DP 共用 `OnShadowChanged` → `UpdateShadow` → 重新赋值 `DropShadow.BlurRadius`。真实 Composition 中，直接赋值终止该属性上正在运行的动画；但 `_breathAnimation` 字段仍非空，`StartBreath()` 的守卫拒绝重启，`IsBreathing` 仍显示为 true。
- 独立实证：同一真实 WinUI 窗口内分别运行本地与固定上游原始类。本地 Color/OffsetX 更新后 `TryGetAnimationController("BlurRadius")` 变为 null，直接 `StartBreath()` 无效，必须先 Stop 再 Start；上游两种属性更新均保持呼吸动画。8 项动态对照断言和 1 项依赖断言通过。
- 同根兼容问题：调用方通过公开 `DropShadow` 设置的其他 native 值被无关 Color 更新覆盖。本地观察到 Offset/Blur/Opacity 回到 DP 值 `<4,0,0>/9/1`，上游保留 `<12,13,14>/18/0.45`。这不是另一项独立 finding。
- 影响：完整移植 API 的组合功能发生回归。当前播放器桌面歌词未开启呼吸动画，因此不将其扩大为已发生的歌词播放故障。
- 修复方向：恢复按属性更新 native 值，优先使用回调新值；加载时的全量初始化可继续集中处理。补充“呼吸中更新颜色、偏移、透明度、异步取色”的真实 Composition 对照回归，并验证公开 native 配置不会被无关 DP 更新抹掉。
- 分类：本次移植引入。上游普通属性回调只写对应 native 属性。
- 证据：`artifacts/UpstreamParityReview/Extra.cs`、`artifacts/UpstreamParityReview/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/result.txt`。

### 2. [P2] 打包应用的图像取色绕过 ms-appx 资源解析

- 定位：`Controls/CompositionShadowImageHelper.cs:46`–`:54`。
- 触发：启用图像边缘取色，来源为打包应用的逻辑资源 URI，例如 `ms-appx:///Assets/Square44x44Logo.png`，实际只有 `.scale-100.png`、`.scale-200.png` 等限定资源；或 URI 的 authority 指向包依赖图中的其他包。
- 调用链：helper 无条件把 ms-appx 转为 `AppContext.BaseDirectory` 下的物理路径 → `File.OpenRead`，从而丢失资源限定符协商与包 authority。基本文件不存在时抛错，外层 `UpdateColorFromImageAsync` 捕获后把颜色回退为黑色。
- 上游差异：`ColorHelperEx.GetImageEdgeColorWithWin2DAsync` 在 packaged 且非 file URI 时直接使用 `CanvasBitmap.LoadAsync(device, uri)`，仅 unpackaged 路径进行本地解析。
- 证据等级：完整静态调用链与官方资源规则；未执行真实 MSIX 安装复现。仓库 Assets 已存在仅带 scale 后缀的这类文件。普通无限定包内文件不能据此认定全部失败。
- 影响：完整 API 在资源限定符及依赖包场景取色错误；当前实际歌词路径仅使用字形 mask，不触发此分支。
- 修复方向：保留 packaged 的 URI 加载；只在 unpackaged 且需要本地路径时转换。补充真实打包的 scale/theme/language 资源与依赖包资源测试。
- 分类：本次移植引入。
- 官方依据：[资源限定符与 ms-appx 加载](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/images-tailored-for-scale-theme-contrast)、[ms-appx 的 authority/path 及内容协商规则](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/uwp/app-resources/uri-schemes.md)。

### 3. [P2，测试] 嵌套阴影卸载断言未等待内层控件完成卸载

- 定位：`_tools/LocalControlsUiRegression/Program.cs:76`–`:86`、`:193`–`:194`。
- 触发：测试卸载嵌套的 outer/inner 阴影。`RemoveAsync` 只等待 outer 的 `Unloaded`，返回后立即要求 inner 的 `DropShadow.Mask` 为 null；内层自身的 `Unloaded` 处理可能尚未执行。
- 主代理实测：当前提交构建成功、0 警告/0 错误。连续 4 次运行同一夹具，1 次 38 项全过，3 次在前 15 项通过后失败于 `Unload detaches the shared mask without disposing it`。结果保留于 `artifacts/DevWinUIReview/local-controls-first-run.txt` 及 `local-controls-run-1.txt`–`local-controls-run-3.txt`。
- 独立复核：仅在临时夹具补充等待 inner 自身卸载状态及 mask 清理后，4 次运行均 38 项通过，未修改生产代码或原测试。这支持夹具存在时序缺口，不支持把原断言失败直接归因于生产泄漏。
- 影响：资源回归不稳定，失败会阻止后续圆角、呼吸和取色断言执行；此前单次“38 项通过”不足以说明这套夹具可靠。该观察本身不是生产资源泄漏证据。
- 修复方向：操作前同时订阅并等待 inner、outer 自身的 Unloaded 完成，保留 UI 上下文与真实事件时序，再断言各自资源状态；不要靠任意延时放宽条件。异常路径也宜设置非零退出码，现有 Run.ps1 仍可通过结果文件中的 FAIL 检出失败。
- 分类：本次新增测试的时序缺陷。

## 功能一致性边界

- 未发现遗漏的公开控件成员、默认值或模板部件。AutoScrollView 的双副本布局、默认速度/间距、停止恢复 XAML、内容/视口变化、卸载重载和换模板均有源码对照及真实控件覆盖。
- 不能将成员名称完整等同于行为完全等价：前两项 finding 均影响支持的 API 组合或部署方式。
- 已记录的有意变化不再作为回归重复报告：`compositor` 改为只读；停止呼吸恢复配置半径；新增卸载清理/重载恢复；原版在 IsRounded=true 后立即设置 Mask 可暂时覆盖圆形 mask，本地始终保持圆形优先。最后一项属于已记录的遮罩优先级变化，而非逐语句复刻上游 setter 时序。

## 资源释放、性能与分配

### [P3，继承局限] 取消 helper 不能保证远程 I/O 已停止

- 定位：`Controls/CompositionShadowImageHelper.cs:43`–`:54`、`Controls/CompositionShadow.cs:381`–`:387`。
- 实证：在真实 WinUI 进程内用本地 HTTP 服务保持图像响应未返回；传入令牌取消 500ms 后，helper Task 已为 Canceled，但服务端观察 TCP 连接仍未关闭；发送响应后 Task 仍为 Canceled。4 次探针观察一致。
- 边界：这证明 Task 取消不等于连接已中止；未据此证明仍在解码、无限保留或永久 socket 泄漏。当前版本检查能阻止迟到颜色发布，位图使用 using 释放；上游同样未提供可取消网络下载，因此不计作本次新引入的功能回归。
- 辅助源码依据：微软 [CanvasBitmap URI 加载实现](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/lib/images/CanvasBitmap.cpp) 及 [AsyncOperation 取消实现](https://github.com/microsoft/Win2D/blob/winappsdk/main/winrt/inc/AsyncOperation.h)。链接指向上游当前分支，不把它冒充已安装二进制的固定源码快照；本机 Win2D 1.4.0 的取消行为以探针实测为准。
- 改进方向：若要求关闭/换源后停止远程下载，使用可传递令牌的 HTTP/响应流边界，再交给 Win2D，并明确在途请求的拥有者和退出责任。验证任务结束、底层 I/O 结束与迟到 UI 发布三个独立条件，不能只检查 CTS 字段为空。

### [优化建议，继承上游] 将滚动合成对象延迟到实际需要时创建

- 定位：`Controls/AutoScrollView.cs:17`–`:57`、`Controls/RedirectVisualView.cs:21`–`:43`。
- 代码事实：即使 IsPlaying=false、从未溢出或从未加载，构造仍创建 surface、brush、两个副本 visual、property set、expression/keyframe animation 和 easing。实际列表每行有三个滚动控件。上游同样如此，不属于移植回归。
- 实测：预热 10 个实例后，UI 线程连续构造 100 个停止滚动的 AutoScrollView，用时 9.390ms、分配 1,433,960 B 托管内存；100 个 ContentControl 对照为 0.179ms、48,624 B。最终计数不含 WeakReference/List 跟踪开销；构造期间对象保持强引用，未把这个实验当作 GC 后保留量或泄漏测量。
- 改进方向：第一次真正溢出并挂接时才创建合成状态；布局模板和 Child 绑定仍可独立工作。对临时虚拟化卸载可保留可复用的小状态，对永久退役另定明确释放入口，避免每次悬停/重载都重建整套资源。先测真实长列表首次显示与连续滚动，再决定释放/复用策略。
- 限制：这是 Debug 微基准，不是完整应用启动/掉帧结果；未量化原生堆、GPU 纹理或实际列表的保留量，不宣称零分配或固定收益比例。

### 保留四边取色方案，暂不为小边缘数组引入池化

- 定位：`Controls/CompositionShadowImageHelper.cs:26`–`:40`。
- 算法保持上游四边平均、角点重复计入和不按 alpha 加权；改成只读四边，避免整图颜色数组和临时 List。自动 mask、调用方共享 brush/clip 与共享 CanvasDevice 不由控件错误 Dispose；自有几何、曲面、画刷和呼吸动画已有明确解除/释放路径。
- 最终微基准：同一 4096×4096 CanvasRenderTarget，预热两种算法后，各取 3 个样本；当前四边读回及平均值计算的时间中位数为 0.902ms、托管分配均值 65,928 B/次；上游整图读回加 List 算法为 72.720ms、67,240,512 B/次。512、2048 像素数据也保留在原始日志。
- 此数字只覆盖同步像素读回及平均值计算，不含图像加载/解码；四边读取仍解码完整图像，不能将托管数组下降等同于 GPU/全进程内存同幅下降。若后续需要限制大图峰值，应单独定义解码尺寸和颜色精度契约。
- 先保留现有清晰的数组所有权；未测得这些短数组造成瓶颈前，不建议为“零分配”引入复杂池化。

### [优化建议，既有调用方] 合并桌面歌词的 mask 刷新队列

- 定位：`DesktopLyrics/TextBlockLyricsRenderer.cs:248`–`:254`。
- 换字/字体/布局可能由三行 SizeChanged 和样式更新重复触发 RefreshShadowMask；当前每次同步 ApplyMasks 后又排一个低优先级回调。委托已缓存，但待执行工作没有合并。
- 可用一个 pending 标记合并同一轮布局的后置刷新，并在退出/卸载后忽略过时工作。应保留必要的立即更新以避免阴影滞后一帧，补真实换字与换行验证。该逻辑在移植前已存在，本次只改类型引用，因此列为既有优化建议，未声称其已经造成可感知卡顿。

测量环境与证据：`.NET 11.0.0-rc.2.26504.105`、Windows `10.0.26300.0`、x64 Debug、Windows App SDK 2.5.1、Win2D 1.4.0。程序与测量代码为 `artifacts/ResourcePerformanceReview/Program.cs`、`Probes.cs`；最终结果 `probe-final.txt`，重复记录 `probe-run-1.txt`–`probe-run-3.txt`。未采集生产 UI 帧时间、STW trace、GC 后存活根链或原生/GPU 高水位，所以本报告不作这些结论。

## 覆盖与验证

| 范围 | 文件数 | 结果 |
| --- | ---: | --- |
| 两控件、基类、属性、取色辅助及模板 | 6 | reviewed；对照固定上游并检查所有权、动态属性和生命周期 |
| App 资源与主项目依赖配置 | 2 | reviewed；DevWinUI 包移除、本地样式与许可输出 |
| DesktopLyrics、LibraryNavigationService、各 View 调用方 | 19 | reviewed；命名空间迁移、原绑定/事件/布局、共享弹窗入口 |
| 新增及调整的回归工程与夹具 | 8 | reviewed；真实 Composition 覆盖及嵌套事件时序 |
| 来源、README、许可与 Changelog | 5 | reviewed；完整范围声明、固定来源和已声明行为变化 |
| 合计 | 40 | reviewed 40；skipped 0 |

- 主代理重新构建并运行 `HomeUiRegression.exe --local-controls`：19 项通过，包括实际 MusicListRowControl 的编译绑定、悬停、取消、行复用、卸载/重载。构建有已有警告，0 错误；日志：`artifacts/DevWinUIReview/home-build.txt`、`shipping-row-result.txt`。
- 原始 LocalControlsUiRegression 构建通过；运行稳定性结果如 finding 3，不把报告中的历史通过替代本轮证据。
- 独立上游行为对照结果如 finding 1。真正 MSIX 限定资源、Windows 10 实机、完整应用逐页人工验收尚未执行。
- 资源复核未执行长时间真实列表滚动、设备丢失恢复、GC trace 或 native/GPU 保留量采集；因此未确认泄漏不等于保证不存在泄漏。
- 本轮没有重新运行文档已记载的首页全量预览断言与旧导航夹具基线失败；不能将此前结果称为本轮通过。


## 2026-11-02 后续独立修复状态

- 用户批准仅修复问题 1；问题 2 按要求暂不处理，`CompositionShadowImageHelper` 未修改。
- 问题 1 已修复：Color、Opacity、Blur 的静态回调只写对应 native 值；偏移只改对应轴并保留其余原生轴。全量配置仅用于构造/加载初始化，避免无关属性更新终止呼吸或覆盖调用方配置。
- 问题 3 仅是夹具把外层 Unloaded 当作整个子树清理屏障；生产每个控件在自己的 OnUnloaded 里解除视觉、停止动画和清理资源，没有使用这一错误假设。当前未发现由该事件先后顺序导致的生产错误或泄漏。修复测试的等待条件，生产卸载代码不变。
- 先新增回归复现旧代码：`Color changes preserve the running breathing animation` 失败；之后应用生产修复，50 项真实 WinUI 回归连续 3 次全部通过，包括呼吸中颜色、三个偏移轴、透明度、异步取色、显式 Blur 更新及调用方 native 值保留。
- 主项目 x64 Debug 构建通过，0 错误，519 个警告；本轮未逐项治理项目构建警告。未触及 AudioPlayer 或 IPC，无需重新发布独立播放进程。
- 证据：`artifacts/CompositionShadowBreathingFix/before-fix.txt`、`after-fix-1.txt`、`after-fix-2.txt`、`after-fix-3.txt`、`main-build.txt`。本节描述审查之后的工作区修复，不改变前文针对原提交的审查事实。
