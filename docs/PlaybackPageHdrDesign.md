# 播放详情页 HDR 显示设计

本文记录主窗口播放详情页的设计、执行工作包及验证结果。场景沿用 Win2D、Direct2D 和 ComputeSharp.D2D1。桌面歌词窗口、任务栏和 SMTC 各有自己的宿主。第 13 节汇总当前实现和实测数据，第 9 至 12 节保留历次调校的基线。

## 1. 页面的合成边界

每个活动的播放页最多有一个 HDR 交换链。标题固定使用 AnimatedTextBlock，歌词固定使用高级 Win2D 渲染器。音量、进度、菜单和对话框继续由 WinUI 合成。

| 播放页状态 | 背景 | 歌词和标题 | 封面 |
| --- | --- | --- | --- |
| HDR 关闭、当前屏为 SDR 或呈现失败 | 当前 SDR 背景或窗口材质 | Win2D SDR 呈现 | 着色器背景下与背景共用 SDR 帧，其他模式显示 XAML 预览 |
| 着色器开启，HDR 可用 | 不透明 FP16 交换链绘制着色器和天气效果 | 歌词、标题、专辑和艺术家绘入同一帧 | 普通图按白点和受限高光曲线映射，支持的原生 HDR 图按原始亮度绘制 |
| 选择自定义背景图，HDR 可用 | 背景图、模糊和主题遮罩绘入不透明场景 | 同一 FP16 帧 | 普通封面、主题占位图、支持的原生 HDR 图均在同一帧绘制 |
| 透明亚克力，无着色器或背景图 | 窗口的桌面亚克力 | HDR 偏好开启时使用可透明合成的 SDR 歌词，标题使用自身 SDR 宿主 | XAML 预览 |

从旧版升级时，先前保存的标题模式、歌词模式和标题悬停滚动开关不再读取。播放页中截断的标题文字支持悬停滚动。动画类型、歌词颜色和逐字样式继续使用用户已有设置。

## 2. 页面接入

- [播放页](../View/PlayingDetailPage.xaml) 保留一个标题布局和一个歌词区域。[展示 ViewModel](../ViewModel/Pages/PlayingDetailViewModel.Presentation.cs) 根据背景决定活动宿主。着色器或图片背景使用 NowPlayingCanvas，无背景图的亚克力使用独立 AdvanceLyricsCanvasControl。两者共用歌词协调器实现，点击歌词仍通过页面命令定位播放。
- [NowPlayingCanvas.cs](../External/AnimatedWin2dControls/AnimatedWin2dControls/Controls/NowPlayingCanvas.cs) 已经先画背景和天气，再调 `LyricsRenderCoordinator.OnDraw`。将场景绘制入口与 `CanvasAnimatedControl` 的更新循环解耦，显式传入设备、DIP 尺寸、DPI、时间、可绘区域和当帧绘制会话。保留现有 `CanvasDrawContext` 的同步借用边界。把协调器对具体控件的输入、时间同步、资源重建依赖留在宿主适配处。
- 在不透明模式使用可控的 `CanvasSwapChain` 和 `CanvasSwapChainPanel`。由同一 `CanvasDevice` 创建交换链、缓存和 ComputeSharp 的 `PixelShaderEffect<T>`。可直接在线性目标上完成的绘制直接写入交换链；需要独立调色、采样和模糊的层才分配离屏目标。一次更新、一次绘制、一次 Present。SDR/HDR 切换复用场景状态，两个宿主不得同时推进同一行歌词或呈现同一标题。
- UI 线程沿用 [PlayingDetailPage.xaml.cs](../View/PlayingDetailPage.xaml.cs) 的 `TransformToVisual` 做法，把歌词、标题、封面区域及裁剪范围以 DIP 快照交给绘制线程。竖屏是歌词铺底、左栏内容在上；如果把封面或标题绘入场景，按此顺序绘制。保留 XAML 的测量占位、键盘焦点、自动化名称、指针命中和事件到 ViewModel 的转发。挂入场景的旧可见控件停止绘图，避免双影和重复订阅。

## 3. 线性色彩与亮度

**输出格式。** HDR 交换链使用 `R16G16B16A16Float`、双缓冲、scRGB（线性、BT.709 原色，即 `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`）。不透明模式每帧清成不透明的主题底色并覆盖全页。FP16 交换链默认对应 scRGB；若接入原生 DXGI 显式设置，则检查 `CheckColorSpaceSupport` 的 PRESENT 位和 `SetColorSpace1` 的结果。按实际格式、创建、挂接和呈现结果报告状态。中间效果链在需要保留高光的位置显式使用 `Precision16Float` 或已有更高精度。[微软 Advanced Color 指南](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range) · [Win2D 支持的像素格式](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/pixel-formats)

**白位和高光。** 窗口移动、尺寸或呈现状态改变时，通过 `DisplayArea.GetFromWindowId` 确定实际输出。跨屏时重新使用 `DisplayInformation.CreateForDisplayId` 获取该显示器的 HDR 状态与峰值，并替换颜色变更订阅。W 为用户设置的参考白点，P 为用户设置的高光峰值，两者分别受显示器峰值限制，并满足 W ≤ P。两者相等时场景保留纸白动态范围。Windows 的 SDR 白点继续用于上层 XAML。[窗口显示信息](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.graphics.display.displayinformation) · [系统白位调整](https://learn.microsoft.com/en-us/windows/win32/direct2d/white-level-adjustment-effect)

- 进入 FP16 场景的 sRGB 输入先解码为线性值。标准白像素为 `W / 80`，目标为 P 尼特的中性高光为 `P / 80`。例如 W = 200、P = 600 时分别是 2.5 和 7.5。XAML 控件留在自己的 SDR 表面，由 Windows 对白；应用只校正自己合成进 HDR 表面的 SDR 像素。
- 背景保留原调色板与连续亮度变化。已唱过的默认白色字芯与静止标题对齐参考白，仅逐字歌词产生辉光，活动字芯的中性白上限为 min(W × (1 + 0.10 × 发光强度), P)。强度 0、5、10 对应参考白的 1、1.5 和 2 倍。模糊核及原有覆盖率沿用相应动画。验收同时检查背景色阶分布、歌词字芯及其周围的亮度。浅色主题的黑字保持黑色。已有线性 HDR 值直接进入线性场景，避免再次进行 sRGB 解码。
- 重点检查 [FluidBackgroundEffect.cs](../External/AnimatedWin2dControls/AnimatedWin2dControls/Shaders/FluidBackgroundEffect.cs) 等着色器的最终 `Saturate`、8 位输出抖动、`NowPlayingCanvas._bgCache` 的默认格式和 RotatingMesh 的最终复合及低精度回退。权重、遮罩、透明度仍可限幅，需要跨过 1.0 的颜色必须在限幅之前保留或独立生成。RotatingMesh 的坐标求解纹理承载数据，沿用其自身精度约束。8 位字形覆盖率、SDR 调色板也可以继续用。暂停、背景跳帧和重用缓存时，最终 HDR 亮度应一致。
- 对已预乘的 sRGB 图层，先去预乘，再线性化、映射亮度、在线性空间重新预乘；透明像素写全零。对 HDR 文字优先用覆盖率遮罩加线性颜色或 HDR 画刷，逐字透明度独立于 RGB 亮度。用灰阶抗锯齿检查透明边缘。参考分支的输出着色器适合处理约定为 sRGB 的输入；给整个已线性化的场景套同一着色器会重复变换。[BetterLyrics-HDR 输出着色器](https://github.com/DrivingGodJ/BetterLyrics-HDR/blob/57763f5a86ebb89e101f13a7e855c072fafd9e4e/src/BetterLyrics.DotNet/BetterLyrics.WinUI3/BetterLyrics.WinUI3/Shaders/ScRgbOutputEffect.cs)

## 4. 封面和 AnimatedTextBlock

**封面。** 着色器或自定义图片背景启用 HDR 时，普通 JPEG/PNG 优先复用匹配原图且已完成色彩管理的显示缓存。缓存缺失或无效时从原图解码。WIC 处理方向和 ICC，生成限尺寸的预乘 sRGB 纹理。ComputeSharp 将原色解码成线性 RGB，以参考白点映射中间调。设线性 Rec.709 亮度为 Y、可用余量 H = P − W，普通封面的额外亮度预算为 E = (0.25 W × H) / (0.25 W + H)。封面曝光增益为 G = 1.10 × (W + E × smoothstep(0.60, 1, Y))。余量充足时，从中间调到图像白都比上一版增加 10% 线性亮度。若 G 超出 P，根据最亮线性通道 M，令 G 减去 max(G − P, 0) × smoothstep(0.60, 1, M)，用同一增益平滑收拢三个通道。W = 300、P = 600 时，sRGB 码值 128 的中灰约为 71.2 nit，纯白约为 396 nit；把 P 降至 300 时，中灰仍为 71.2 nit，纯白降至 300 nit。此曲线是播放页的观感选择。三个通道使用相同增益，保留原图的色相与透明覆盖率。原图读取失败时依次尝试显示缓存和当前主题的占位图，两者都可进入 HDR 交换链。

带全范围 BT.2020/PQ cICP 标记的 PNG 原图单独解码成线性 scRGB。HDR 按目标峰值压缩高光，SDR 将高光映射到 SDR 范围并编码为 sRGB。页面在首次测量时确定横竖布局，XAML 和场景共用布局框与等比缩放规则。着色器模式下，两种输出都用同一套圆角、阴影和换曲过渡。场景首帧就绪后交接 XAML 预览，切换着色器场景的动态范围时保留过渡进度。退到图片背景 SDR 或透明亚克力时由 XAML 显示封面。切歌或退出场景时取消过期请求。[原图解码](../Helper/PlaybackSceneCover.cs) · [场景绘制](../Helper/PlaybackSceneCoverDrawing.cs)

**动态标题。** 两种不透明 HDR 背景下，播放页使用 AnimatedTextBlock 的原有排版、字形差分、动画与悬停滚动，将其绘入同一 FP16 场景。标题、艺术家与专辑共用白点和实际前景色。系统默认前景在 HDR 中使用实色，副标题保持原有层级透明度，自定义前景仍保留用户给定的颜色和透明度。静止和转场都将原文档的像素按同一参考白映射，保留淡入、擦除、模糊各自的时间曲线和覆盖率。辉光只在逐字歌词中绘制。Windows 关闭动画时，同一个控件直接显示当前曲目信息。

## 5. 透明亚克力和 HDR 同时开启

WinUI 3 的 `SwapChainPanel` 是外部内容。微软明确列出它的透明和采样限制。将全页面板清为透明，或把 `AcrylicBrush` 盖在面板上，均不能作为可靠的桌面透视或背景模糊方案。项目里的桌面材质由 `Window.SystemBackdrop` 和 `CustomAcrylicSystemBackdrop` 提供，与页面内模糊应用内容的 `AcrylicBrush` 是两条合成路径。在不透明 HDR 背景上弹出菜单时，直接使用足够不透明的主题底色即可保证文字可读。如果需要控制条局部模糊着色器画面，可在同一 Win2D 场景对已有背景纹理的对应区域模糊，XAML 按钮叠在上方。[SwapChainPanel 限制](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel) · [WinUI Visual Layer 的外部内容规则](https://learn.microsoft.com/en-us/windows/apps/develop/composition/visual-layer)

**可先发布的路径。** 退出不透明背景时撤下全页 HDR 面板，保留窗口材质和用户的透明度设置，并显示“透明亚克力 · SDR 前景”。高级歌词继续复用同一协调器。如果独立 `CanvasAnimatedControl` 在目标设备上也挡住桌面，透明 SDR 呈现端使用可参与 XAML 合成的 `CanvasControl` / `CanvasImageSource` 绘制，在歌词动画期间请求下一帧。即使回退也保留点击定位、滚动、歌词遮罩和转译文。透明效果被系统禁用时遵守原有材质回退。[Win2D 的呈现选择](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)

**必须同时呈现桌面亚克力和 HDR 歌词时。** 单独验证系统合成宿主。先用独立测试 HWND 取得由应用拥有的原生合成树，底层接系统桌面亚克力，中层用原生 DirectComposition visual 承载 `CreateSwapChainForComposition` 创建的预乘 FP16 交换链，前景只画歌词、选定的标题效果和 HDR 封面。Win2D 仍使用同一 D3D 设备及该交换链的绘制目标，ComputeSharp 继续生成效果。顶部保留透明的 XAML 交互层。由 DWM 对透明区域合成桌面，应用无需读取桌面像素。此方案要求应用实际拥有且能控制各层顺序；若现有主窗口的 WinUI 树无法插入这一层，应先用自有 Win32 宿主与 XAML Island 证明组合，再决定是否值得迁移主窗口。不要向 WinUI 已拥有的合成目标强行设置根节点。系统材质控制器只挂在一个所有者上，系统关闭透明效果时保留其回退行为。[DXGI composition 交换链](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforcomposition) · [Win32 系统材质目标](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.systembackdrops.isystembackdropcontroller.settarget)

这条组合路径是待证实的工程方案。普通 `SpriteVisual + CompositionDrawingSurface(FP16)` 也需要检查最终屏幕，Windows App SDK 1.8.5 的公开复现记录了这种路径在合成后丢失高光。本项目引用 2.6.5，不能直接外推那个版本的实测结果。另已检查本项目 Win2D 1.4.0 的 WinMD，未导出部分旧示例所用的 `CanvasComposition.CreateCompositionSurfaceForSwapChain`。仅在当前版本证实桌面材质、HDR 亮度和弹层顺序同时正确后，应用才将这一模式标为 HDR 生效。[Windows App SDK 复现记录](https://github.com/microsoft/WindowsAppSDK/issues/6291) · [Win2D 发布记录](https://github.com/microsoft/Win2D/blob/winappsdk/main/CHANGELOG.md)

## 6. 设置、切换与资源所有权

- “封面与背景”中的“播放页 HDR”使用 SettingsExpander。标题行开关默认关闭，内含参考白点 80–1000 nit（默认 300）和峰值 80–4000 nit（默认 600）。升高白点超过峰值时一并升高峰值，降低峰值低于白点时同步降低白点。旧配置保留已存峰值。歌词设置中的发光强度范围为 0–10，默认 5；同一档位在 HDR 下额外提高逐字歌词的发光亮度，模糊宽度与覆盖率沿用原动画。读取过渡版本的配置时沿用已有发光强度，忽略曾经独立保存的两个 HDR 增益。
- 偏好跟随现有 `AppearancePreferencesState → SaveSettings / SettingsSnapshotFactory` 及设置加载路径。窗口级显示状态单独管理；ViewModel 发布已请求、实际输出、限制原因和有效峰值，设置页用 `x:Bind`，状态资源在七种语言使用独立键。切到 SDR 屏时保留用户选择，返回 HDR 屏自动恢复。拖动滑条只更新下一帧的常量，合并磁盘写入并在正常退出时提交最后值。
- 首次显示、跨屏、`AdvancedColorInfoChanged`、DPI/尺寸变化、恢复可见和设备丢失触发重新评估。UI 线程负责读取窗口及挂接、摘除、发布绑定状态；呈现端串行拥有 GPU 资源。切换先准备新目标的首帧，再交接，旧代迟到的挂接、解码与状态通知作废。退出时停止时钟、解除订阅、等待在途绘制、摘除面板，再释放其持有的资源。页面最小化或隐藏时停止 Present；用户暂停音乐后，按各效果是否仍需动画决定要不要绘制。
- 以现有帧率和背景跳帧策略为基线。4K 单张全屏 FP16 约 63.3 MiB，双缓冲约 126.6 MiB，还未计场景缓存和效果临时目标。优先直接绘制，按需创建中间目标，保留歌词原分辨率并单独调整背景分辨率。分别测量 GPU 毫秒、Present 间隔、显存和暂停态活动，再决定缓存策略。

## 7. 实施顺序与发布验收

| 阶段 | 验证方法 | 通过条件 |
| --- | --- | --- |
| ① 呈现原型 | 在项目实际 SDK 上，先用不透明交换链画 XAML 白的对照块及三档亮度，另在透明宿主画透明、半透明测试块 | 以场景白点 200 nit、有效峰值至少 600 nit 为例，200/400/600 nit 对应 2.5/5/7.5 中性 scRGB 输入。用 FP16 显示器捕获检查合成结果，XAML 白与系统白参照相符，超过系统白的两档可区分。只读回应用纹理不足以证明上屏亮度 |
| ② 背景和高级歌词 | 测试七种着色器、图片加载中和失败、跳帧开关、天气、浅深色歌词、逐字和译文，以及无逐字时标的歌词 | 高光随滑条变化，普通白稳定；图片未就绪有明确底色；无黑屏、泛白、边缘黑圈或两个宿主同时绘制 |
| ③ 标题和封面 | 普通白底封面、带配置文件的 SDR 图、实际支持的 HDR 样张、缺图，快速切歌、缩放、横竖布局 | 普通封面由白点控制中间调、峰值控制高光，HDR 样张的已知高光保留；过渡、阴影、裁剪、塑形与悬停正常，旧封面不会迟到覆盖新曲目 |
| ④ 亚克力组合 | 在播放器后方移动带明显色带的窗口，测试 0、0.5、1 覆盖率和高光字形，并打开歌词菜单、设置、音量弹层 | 透明区实时呈现桌面材质，半透明边缘颜色正确，高光超过系统白，弹层、焦点和点击顺序正确；任一条件不成立即使用亚克力 SDR 前景 |
| ⑤ 发布与生命周期 | 全新输出目录 Release/x64 剪裁发布；在 HDR 屏、SDR 屏、混合 DPI 及能复现设备重建的环境下测试，验证三种背景与设置持久化 | 跨屏与切换 Windows HDR 后状态准确，设备重建能恢复，最小化无持续 Present，反复切换和退出无遗留句柄或后台帧；七语言资源和 XAML 静态检查通过 |

使用 Windows Graphics Capture 时应选取显示器并保持捕获链为 FP16。这样可以检查应用、XAML 与 DWM 合成后的信号。测量面板实际发光亮度需要光度计及指定面积的测试块。第 9 节记录本次显示器采集。[Windows HDR 捕获说明](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) · [BetterLyrics-HDR 呈现实现](https://github.com/DrivingGodJ/BetterLyrics-HDR/blob/57763f5a86ebb89e101f13a7e855c072fafd9e4e/src/BetterLyrics.DotNet/BetterLyrics.WinUI3/BetterLyrics.WinUI3/Renderer/HdrLyricsPresenter.cs)

## 8. 执行工作包

从仓库根目录执行，适用根目录 AGENTS.md。各工作包的通过条件分别记录。一次 GPU 回读可以验证计算，只有合成后的显示器采集或实机测量可以验证物理显示路径。

| 顺序 | 修改入口与实施内容 | 交付检查 |
| --- | --- | --- |
| A　显示与偏好 | 沿 `AppearancePreferencesState`、`PreferencesState`、`AppViewModel`、`SaveSettings`、`SettingsSnapshotFactory` 及设置加载逻辑接入 HDR 开关、参考白点与峰值。窗口持有显示信息；保留请求状态、当前能力和实际呈现结果。在 `CoverBackgroundSettingsControl` 中连接三项偏好和状态文案 | 缺少 HDR 偏好的旧配置默认为关、参考白点 300 nit、峰值 600 nit；往返保存、非法数值、拖动、搬至 SDR 屏后仍保留请求；七语言齐全 |
| B　不透明呈现 | 在 AnimatedWin2dControls 中接入显式 FP16 双缓冲呈现，整合 NowPlayingCanvas 现有单次场景绘制、缓存和歌词前沿。把亮度映射放到域明确的位置。释放、resize、模式切换与设备丢失均经过同一所有者 | 隔离绘制验证黑色、纸白与高光，覆盖缓存开关、明暗主题和不透明度；实际呈现成功才上报 HDR |
| C　页面集成 | 在 PlayingDetailPage 和展示 ViewModel 中接入显示配置、呈现状态与宿主生命周期。覆盖着色器、图片和透明亚克力三个实际入口；保留原来的布局、输入和暂停行为 | 反复切换及退出无重复宿主、后台帧或过期回调；关 HDR 能回到原来的画面 |
| D　标题与原图封面 | 依第 4 节映射普通封面的中间调与高光，并按场景白点绘制标题正文；把原有标题动画按参考白直接绘入同一场景。对能够确认的 HDR 原图实现独立于八位缩略图的解码与呈现，记录已支持的输入。验证缺图、迟到解码、过渡与设备重建 | 分列普通 SDR、原生 HDR、未支持编码的实测结果，检查原图高光和旧宿主切换；保留 XAML 命中与自动化 |
| E　透明材质 | 产品路径先接妥可透明合成的 SDR 回退；验证原生预乘 FP16、桌面材质和 XAML 三层合成原型，记录操作系统、SDK、显示模式、实际采集结果。没有所需显示器时继续其余集成和回归，保留可复现步骤 | 仅当第 7 节的透明度、高光、Z 序和输入全部通过才向用户显示透明前景 HDR 生效；其他情况下准确显示 SDR 前景 |
| F　发布与审查 | 更新统一 Changelog。针对改动运行设置、动画、歌词和 Win2D 回归；静态核对新资源键及编译绑定、CRLF；执行新目录的 Release/x64 剪裁发布并从本次产物验证 | 交付实际命令、结果、失败原因、回退策略和待设备验证项；把编译、GPU 缓冲、合成输出三种证据各自标清 |

已有回归入口包括 `_tools/SettingsPersistenceRegression`、`_tools/LyricsRenderRegression`、`_tools/AnimatedTextRegression/Run.ps1`、`_tools/AppearanceUiRegression/Run.ps1` 和 `_tools/DrawPathProbe/Run.ps1`。先读各自入口确认是否使用真实生产代码，再补缺少的断言。发布可沿用以下仓库命令，每次使用新的运行标识。

```powershell
$hdrRun = [Guid]::NewGuid().ToString('N')
dotnet publish WinUIMusicPlayer.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishTrimmed=true "-p:IntermediateOutputPath=obj/PlaybackHdr-$hdrRun/" "-p:OutputPath=bin/PlaybackHdr-$hdrRun/" -o "artifacts/playback-hdr/$hdrRun/publish"
```

## 9. 前一版的基线测量

| 场景 | 实现与观测 |
| --- | --- |
| 设置与输出 | 默认关闭，参考白点 300 nit，峰值 600 nit。状态来自当前窗口的显示信息和呈现端的首帧结果。不具备 HDR 输出时保留用户偏好。 |
| 不透明背景 | 本机 Windows build 26300、Windows App SDK 2.6.5、Win2D 1.4.0，3840×2160、150% 缩放。系统报告 HDR、SDR 白 240 nit、峰值 1800 nit。显示器级 FP16 采集得到 2.500、5.000、7.500，分别对应 200、400、600 nit 的输入。 |
| 背景与歌词 | 生产渲染器固定白点 200 nit、峰值 600 nit，对七种背景分别使用暗色、亮色和暖棕调色板。暖棕 LiquidFlow 中亮于 1.05 倍白点的面积从 16.94% 降至 1.30%。已唱字芯跨词保持 4.648 scRGB（约 372 nit），未扫过字形约 2.50（200 nit）。开启辉光后，字芯和底色的最大下降均为 0，字形外测到附加光。 |
| 标题与封面 | 动态标题复用原控件排版、滚动和动画，在系统 SDR 白点 240 nit、应用白点 300 nit 的窗口级 FP16 采集中，静止字形为 3.750 scRGB，转场字形超过该值。普通标题保留 XAML。普通封面在着色器 HDR 模式进入场景。首版原生封面支持带合法 cICP 的全范围 BT.2020/PQ RGB PNG。4 像素原图经 WIC 解码、GPU 映射后，600 nit 压到 400 nit，同时保留 200 nit 和半透明覆盖率。支持的普通 SDR 图由 WIC 解码并按白点与峰值映射。切歌先复用 ImageSwitcher 过渡，待新预览稳定后淡入解码好的 HDR 原图。 |
| 错误恢复 | 自定义图片背景且不使用高级歌词时，SDR 背景属于窗口。向 HDR 帧注入异常，实测原来会残留旧画面。修复后窗口绿色对照块正常显示为 (0, 3, 0)，重试后同一控件恢复 HDR。另验证暂时隐藏窗口时 HDR 帧计数停止。 |
| 透明材质 | 原生 HWND + 预乘 FP16 + XAML Island 的半透明像素约为 4.727，XAML 绿块为 3.000 且弹层能开启，但后方切换红蓝时透明区始终为灰色 1.954。该原型留在测试工具。产品模式撤下全页交换链，使用 `CustomAcrylicSystemBackdrop` 和透明 Win2D `CanvasControl`；实测透过该歌词宿主的采样从红色约 (1.43, 0.02, 0.01) 变为蓝色约 (0.02, 0.03, 2.41)。 |

同一张 JPEG 在 200/600 nit 下，暖肤色通道原尺寸可到 4.824 scRGB，旧路径放大十倍时截为 1.0。显式指定 `CanvasDrawingSession.EffectBufferPrecision = Precision16Float` 后，Direct2D 在重采样时保留 HDR 数据。探针覆盖 0.5 倍、10 倍缩放、96/144 DPI 和圆角。将普通 JPEG 画在真实着色器上时，300/600 nit 配置下窗口采到中灰 0.679、暖肤色 (5.488, 3.609, 2.176)、纯白 7.500 scRGB。

最终剪裁产物已通过程序集哈希校验和 144 DPI 窗口级 FP16 采样。相同版本的显示器级复测因另一窗口遮挡采样位置而中止。表中的桌面材质透视结果来自此前显示器级采集。

从仓库根目录运行下面两个探针。`-PublishedControls` 指向上一步新发布的 DLL，隔离宿主在运行时核对所加载程序集的 SHA-256。`-SampleDisplay` 会暂时显示红蓝测试窗口，仅采样已知窗口坐标。其他应用遮挡测试区时，可用 `-SampleWindow` 独立采集探针窗口的 FP16 合成结果。桌面亚克力透视检查需要显示器采集。原型的 `desktopVisible=False` 会保留在输出中，作为选择产品回退路径的依据。

```powershell
pwsh -NoProfile -File _tools/DrawPathProbe/Run.ps1 -PlaybackHdr
pwsh -NoProfile -File _tools/PlaybackHdrProbe/Run.ps1 -SampleDisplay -PublishedControls "artifacts/playback-hdr/$hdrRun/publish/AnimatedWin2dControls.dll"
```

后续硬件验收包括跨 HDR/SDR 双屏与混合 DPI、设备拔插或驱动重置、光度计测量以及完整播放器持续播放时的性能与显存。单屏隔离窗口测试可复现材质及亮度，不能覆盖以上环境。

本次发布验证还运行了设置持久化、窗口外观、自带曲目样例的高级歌词、动态文字与 SMTC 封面回归。动态文字包含 99 项断言。在 144 DPI 的离屏 GPU 目标上，标题放大 1.3 倍后，指定 FP16 的场景会话保留字芯 2.500、转场 5.414、收尾 2.500 scRGB，旧配置的字芯截为 1.000。暖色 Fluid 和 LiquidFlow 各连续采集 12 帧，动画正常推进，复用缓存时与直接绘制结果一致。静态检查覆盖 7 个语言资源文件中的全部 9 个 HDR 键，以及本次修改的 XAML 编译绑定。发布产物在独立窗口加载和运行，未打开用户曲库做整机播放验收。

## 10. 上一版参考白、普通封面与多显示器的测量

白色已唱歌词、静止歌名与转场结束的歌名使用同一参考白。默认未扫部分保留原歌词不透明度。200/600 nit 配置下，GPU 回读的已唱、默认未唱、辉光最亮像素分别是 2.500、1.250、2.648 scRGB。白点切换到 320 nit，已唱字芯为 4.000。将峰值降到白点，辉光没有额外提亮余量。淡入淡出过程中，字形透明度仍按动画进度变化。

在应用白点 300 nit、峰值 600 nit 下，256 级 SDR 灰阶经过当时的曲线后逐级递增，最亮白为 360 nit。普通 JPEG 在着色器之上的窗口级读数为灰 0.679、暖肤色 (3.893, 2.561, 1.543)、白 4.500 scRGB。这是图像中原有亮部的温和扩展。原生 PQ 封面继续依照自身亮度绘制。

跨显示器时，窗口把当前输出标识和有效白点、峰值一起送入播放场景。移至 SDR 屏后撤下 FP16 面板并恢复 SDR 帧，返回 HDR 屏按当前 DPI 和客户区大小挂入新交换链。硬件往返测试可用下列命令。探针在真实 HDR、SDR 输出间移动同一 HWND，记录帧数、当前 DPI、浮点缓冲像素尺寸和五个窗口采样点，还从后台线程验证 Win32 消息响应。

```powershell
pwsh -NoProfile -File _tools/PlaybackHdrProbe/Run.ps1 -MoveAcrossDisplays -PublishedControls "artifacts/playback-hdr/$hdrRun/publish/AnimatedWin2dControls.dll"
```

最终 Release/x64 剪裁版渲染程序集通过哈希校验。在 3840×2160、150%（144 DPI）的 HDR 屏与 1440×2560、125%（120 DPI）的 SDR 屏间往返两次。窗口逐步越过边界，场景同时包含逐字歌词、标题、普通 JPEG 和雪景。两轮 SDR 的中心及四角均为 1.000 scRGB，HDR 返回后五点均为设定的 3.000，封面回读也与出发前一致。每次移动后 HWND 均在超时前响应后台线程发来的消息。单独改变输出标识，同时保持白点、峰值、DPI 相同，探针也验证了交换链会替换。这里使用隔离播放窗口；旧渲染器在这组测试中也曾成功往返，现有记录不足以确定用户原始冻结的线程栈。

## 11. 上一版固定辉光和 HDR 设备释放的测量

- 默认白色已唱字芯和静止标题使用用户白点 W。标题转场和有逐字辉光的音节共享上限 min(1.15 × W, P)。活动结束后回到 W。暗色字依据实际前景生成暗晕，保留浅底上的边缘对比；自定义色沿用同色相。
- W=200、P=600 nit 的 FP16 回读中，标题和歌词最亮均为 2.875 scRGB（230 nit），稳定后均为 2.500（200 nit）。P=206 nit 时两者读为 2.574（约 206 nit）。标题覆盖 96 与 144 DPI。浅底黑字的实心采样保持为零。
- 关闭 HDR、退出播放页或实际卸载时，播放宿主停止呈现，摘除交换链，释放所属背景、天气和歌词缓存，标题清理设备相关的排版、画刷与辉光，封面恢复 XAML 预览并丢弃旧纹理。亮度调整不重建设备。短暂最小化会停帧，恢复时复用设备。
- 显存验收使用 `_tools/PlaybackHdrProbe/Run.ps1 -CheckMemory -PublishedControls <本次发布的 AnimatedWin2dControls.dll>`。同一进程查询所有硬件适配器的 DXGI 本地与非本地使用量，测试两轮切换、离开、重新进入、卸载、异常和重试，期间不触发强制 GC。旋转网格另有由 ComputeSharp 终结器管理的原生纹理，具体回收时点取决于运行时及驱动。

本次 Release/x64 剪裁渲染程序集在 2560×1440 测试窗口、两块硬件适配器下的进程本地显存如下。使用旋转网格并启用雾和雪；每个关闭读数都在确认 HDR 帧停止后采集。

| 状态 | 第一轮（MiB） | 第二轮（MiB） |
| --- | ---: | ---: |
| 初始 SDR 基线 | 412.0 | — |
| HDR 播放 | 862.2 | 864.3 |
| 关闭 HDR，恢复 SDR 播放 | 433.2 | 433.2 |
| HDR 重新启用后退出播放页 | 452.3 | 452.2 |
| 再次进入后卸载控件 | 471.6 | 471.4 |

独立 Fluid 对照中，初始 SDR 本地显存 236.4 MiB，HDR 约 519 MiB，两轮关闭、退出或卸载后均为 236–237 MiB。两个真实显示器的 144/120 DPI 往返、窗口消息响应和封面采样亦已复测。
显示器级 FP16 复测得到 200/400/600 nit 对照块分别为 2.500/5.000/7.500 scRGB。播放器亚克力后方的红蓝色块切换时，透明歌词前景的同一点从 (1.434, 0.022, 0.014) 变到 (0.015, 0.027, 2.414)。

## 12. 前一版独立光晕增益和单交换链封面的测量

发光强度 5、白点 200 nit、峰值 600 nit 时，白色静止标题与已唱字芯均为 200 nit。进入辉光动画时，亮度增益 15% 和 35% 分别使活动字芯上限达到 230 nit 和 270 nit；使用 35% 增益并将原发光强度调至 10 时，上限为 340 nit。将有效峰值降至 206 nit，字芯约为 206 nit，较暗背景上仍能看到光晕。亮度增益和光晕强度分别控制字芯的提亮幅度、字形以外的发光；主发光强度同时控制两者。

在相同歌词、字形位置与播放时刻读取 GPU 像素。将 SDR 解码为线性值，将 HDR 按参考白归一，然后统计字形覆盖区域之外、增量超过纸白 1% 的像素。默认强度 5、亮度增益 +35%、光晕 150% 时，SDR 有 1,732 个这样的像素，HDR 有 3,888 个。两者字外最亮增量分别为纸白的 44.0% 与 76.6%。将光晕强度调至零后，字芯仍达 270 nit，阈值以上的字外像素归零。默认设置的标题在 96 DPI 和 144 DPI 下同样达到 270 nit，转场后恢复 200 nit；浅底的默认黑色标题字芯保持黑色。

在 144 DPI 的 HDR 显示器上，窗口级 FP16 采集使用 32×20 的普通双灰阶封面，白点为 200 nit、有效峰值为 600 nit。着色器横向、自定义图片横向及纵向三种布局均得到白色 3.359、灰色 0.966 scRGB。在本机系统 SDR 白为 240 nit 时，关闭 HDR 和退回透明亚克力后分别回到 XAML 的 3.000、1.054。首个可见 HDR 封面与稳定帧、XAML 回退之间的边界差在 1.5 DIP 以内。恢复旧的“只有着色器允许普通封面”分支，同一图片背景会采到两个 0.032，测试按预期失败。

用 `_tools/DrawPathProbe/Run.ps1 -PlaybackHdr` 检查逐字扫光，用 `_tools/AnimatedTextRegression/Run.ps1` 检查标题。用 `_tools/PlaybackHdrProbe/Run.ps1 -CheckPageCover -PublishedControls <发布的 AnimatedWin2dControls.dll>` 验证封面分支与首帧尺寸，用 `-CheckHandoff` 检查页面转场、首帧失败和 SDR 回退。后两项使用独立 WinUI 窗口，也可在没有用户曲库的机器上运行。所有亮度读数来自渲染输出或 Windows 合成捕获，面板实际发光亮度仍需光度计测量。

本轮 Release/x64 剪裁产物同时带有两张主题占位封面。独立 WinUI 窗口加载该版渲染程序集后，在 144 DPI HDR 屏与 120 DPI SDR 屏间往返两轮。两端的窗口中心和四角读数分别为 3.000 和 1.000 scRGB，每次切换后窗口都响应消息并继续绘制。开启旋转网格与天气的 2560×1440 场景中，两轮本地显存从 863.7 / 864.5 MiB 降至关闭 HDR 后的 434.4 / 431.5 MiB，初始 SDR 为 412.7 MiB。测试保留了当前驱动和 Win2D 缓存，未对用户曲库做持续播放测量。

完整设置控件的隔离剪裁窗口也验证了两项编译绑定。关闭 HDR 时滑条禁用，开启后可调，分别设为 +67% 和 221% 后，界面数值和偏好状态同步；再次关闭 HDR 保留两项数值。在真实 HDR 窗口把增益从 0% 调到 +35%，同一交换链的活动标题字芯从 3.750 变为 5.062 scRGB，对应白点 300 nit 和辉光约 405 nit。

## 13. 当前版本的标题、歌词、封面运动与 HDR 入场

| 对象 | 最终行为 | 验证方法 |
| --- | --- | --- |
| 歌名、专辑与艺人 | 将原版 AnimatedTextBlock 的当前帧按参考白绘制一次。淡入、擦除、模糊和悬停滚动沿用原版，发光强度只传给歌词。 | 200 nit 参考白下，96 DPI 及 144 DPI（缩放 1.3 倍）的静止、擦除转场、收尾均为 2.500 scRGB。原尺寸逐像素比对原文档和 HDR 输出，覆盖字芯、半透明边缘和全透明背景。 |
| 逐字歌词 | 已唱过的白色字芯稳定在参考白。活动音节按已有设置线性提亮，每档增加参考白的 10%，在有效峰值处限幅。普通散焦采用原亮度，高斯模糊按实际半径向外留出三倍标准差的绘制范围。 | 200 nit 白点、600 nit 峰值下经实际歌词设置总线切换 0、5、10 档，活动字芯为 200、300、400 nit。将峰值降到 206 nit 后读数约为 206 nit。相同动画、相同 FP16 精度下，HDR 与 SDR 光晕覆盖率的逐像素差为零。日文主行、注音和译文的退场均检查边缘。 |
| 封面换图 | FP16 场景的淡入淡出、滑动与缩放复用 XAML 的 350 ms 时长，对透明度、位移与缩放使用同一条三次贝塞尔曲线，控制点为 (0.8, 0) 与 (0.2, 1)。首次显示直接就位。 | 在 144 DPI 捕获实际 XAML 控件的三个转场，再在固定时刻读取 Win2D 画面。动画时间的 25%、50%、75% 对应新封面进度约 4.76%、50%、95.24%。 |
| 播放页上下滑 | 着色器或图片背景的 HDR 首帧成功提交且封面就位后，以原有的 300 ms 指数缓动从底部滑入。上滑期间整页保持不透明，上一页留在下层。上下滑期间，背景、歌词、标题和封面继续绘入同一个 HDR 场景，最多每秒 60 帧，受用户的帧率设置约束。后台一次只提交一帧，完成后由 UI 线程发布画面。HDR 标题在转场前后都使用同一交换链的参考白，重复进入同一首歌时只重绘。下滑保留 HDR 到页面隐藏，随后释放呈现资源。关闭系统动画后直接显示。取消或发生呈现故障时结束本次运动，SDR 和亚克力使用原有入场路径。 | 在 144 DPI 的独立 WinUI 窗口通过生产导航服务进页。Windows Graphics Capture 在同一列的上、中、下三处逐帧采样，检查新页面的到达顺序、滑入中间帧及 HDR 亮度，同时覆盖延迟准备、取消、故障和重开。 |

本次全新 Release/x64 剪裁控件在独立播放窗口中通过着色器、自定义图片背景和亚克力回退测试。在 144 DPI HDR 与 120 DPI SDR 屏之间往返两轮，窗口均保持响应。2560×1440 旋转网格、雪和雾场景下，两轮关闭 HDR 时，DXGI 测得该进程的本地显存分别从 862.7、865.3 MiB 回落至 434.4、433.9 MiB，初始 SDR 为 412.2 MiB。注入绘制失败后回落至 413.9 MiB，重试后恢复绘制。测试覆盖两个启停周期。

带注音和译文的逐字歌词在 1100×700 离屏目标上，每帧托管分配由 15,084 B 降到 916 B。EventPipe 对应的逐字和副行效果精度 setter 分配栈消失。144 Hz 合成回调的可重复调度测试也验证了 60、120 FPS 两档的出帧率。

在这台 NVIDIA 机器上，录屏期间持续向正在滑动的 HDR 页面提交帧，曾使 UI 线程阻塞在 Win2D 的 DXGI Present 调用中。独立录屏进程在窗口实际可见、位于前台时复现了约三秒的延迟。改为让合成器移动已提交的帧后，外部 Windows Graphics Capture 连续三轮采集冷开、热开和延迟恢复，共九次均观察到从下至上的完整 HDR 画面，窗口消息零超时。窗口内采集另验证了 200 nit 参考白的绿色标记在上滑中保持 2.500 scRGB，以及结束后连续绘制恢复。

复测时先按第 8 节发布，再运行以下命令。将 `$publishedControls` 设为这次发布的 `AnimatedWin2dControls.dll` 的完整路径。窗口探针核对所加载的剪裁控件程序集哈希。其余两个探针在可控的原生绘制时刻回读像素。

```powershell
pwsh -File _tools/AnimatedTextRegression/Run.ps1
pwsh -File _tools/DrawPathProbe/Run.ps1 -PlaybackHdr
pwsh -File _tools/PlaybackHdrProbe/Run.ps1 -CheckHandoff -PublishedControls "$publishedControls"
pwsh -File _tools/PlaybackHdrProbe/Run.ps1 -CheckCoverMotion -PublishedControls "$publishedControls"
```
