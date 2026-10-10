# 功能变更记录

新条目加在最上方。

## 2026-10-10 独立播放进程启用 Satori GC
- `External/AudioPlayer/AudioPlayer.csproj`：添加 `PublishWithSatoriGC` SDK（`11.0.0-satori.37965325036.1`），NativeAOT 发布使用 Satori GC。
- `Player/AudioPlayer.exe`：重新发布 win-x64 Release 产物，供主程序打包部署。
验证：NativeAOT 发布、真实 WASAPI 共享输出冒烟测试及网络播放集成回归通过；未执行 ASIO/独占输出及长时间性能对比。

## 2026-10-09 修复播放封面原生资源持续增长
- `Helper/ImageHelper.cs`：文件、字节和默认封面统一解码为可显式释放的 `SoftwareBitmapSource`，取消或失败时释放临时 `SoftwareBitmap`。
- `Helper/ImageHelper.cs`：解码取消统一抛出 `OperationCanceledException`；最长边/固定宽缩放显式计算宽高，并按未旋转原始像素应用（与 WIC 及展示缓存语义一致，EXIF 旋转封面不被拉伸），字节路径恢复与缩略缓存一致的固定宽解码（窄图不上采样）。
- `Controls/ImageSwitcher.xaml.cs`、`Behaviors/FadeImageBehavior.cs`、`Behaviors/AlbumCoverBehavior.cs`：补齐切换、取消、卸载和异常路径的图片源所有权转移与释放。
- `Utils/CoverLoadQueue.cs`：修复创建者取消时残留 waiter 阻止共享请求取消的问题，并释放竞争失败的请求。
- `_tools/SmtcCoverUiRegression/Program.cs`：增加图片源类型、快速切歌、卸载和弱引用回收回归检查，并恢复最长边/固定宽限尺寸解码断言。

## 2026-10-08 修复长时间播放时封面资源持续增长
- `Controls/ImageSwitcher.xaml.cs`：切换、取消、卸载和动画结束时显式释放不再被控件持有的封面图片源。
- `Helper/ImageHelper.cs`、`Behaviors/FadeImageBehavior.cs`：解码失败或取消时清理临时图片源引用，释放可关闭的 WinRT 图片源。
- `Utils/CoverLoadQueue.cs`：封面解码队列改为有界并支持消费者取消，移除永久字符串驻留的缓存键。
- `Services/CoverPresentationService.cs`：过期或取消的系统媒体封面更新主动清空待发布的原图字节。

## 2026-10-07 任务栏音量图标支持点击静音
- `DesktopLyrics/DesktopLyricsWindow.xaml`：`TaskbarVolumeIcon` 外层套上按钮，命令复用 MainPage 音量弹层内静音按钮的 `VolumeSliderIconButtonChangedCommand`，尺寸样式对齐相邻切歌按钮，点击任务栏音量图标即可切换静音。

## 2026-10-07 任务栏封面图移入播放/暂停按钮
- `DesktopLyrics/DesktopLyricsWindow.xaml`：`TaskbarCoverImage` 移入 `TaskbarCoverPlayPauseButton` 内容，封面与播放图标同层，成为按钮实际内容。
- `Style/BtnStyle.xaml`：`TaskbarCoverPlayPauseButtonStyle` 模板在 `ContentPresenter` 之上新增 `StateOverlay` 状态层，PointerOver/Pressed/Disabled 压暗背景改画在该层，保持封面悬浮压暗与禁用置灰效果；封面随之获得按压缩放反馈。

## 2026-10-07 修复播放会话与 ASIO 输出退役时序
- `External/AudioPlayer/Playback/Session.cs`、`GaplessPreloader.cs`：会话清理等待解码线程完成后再继续无缝预载，避免旧会话资源与新会话重叠。
- `External/AudioPlayer/Interop/AsioHost.cs`：ASIO 初始化或回调超时后转入后台退役，等待驱动安全返回再释放驱动、回调表和隐藏窗口。
- `_tools/PlaybackSwitchRegression/Program.cs`、`ReviewFixTests.cs`：增加可单独运行的 ASIO 超时退役回归验证。

## 2026-10-06 修正深色主题封面按钮对比度
- `Style/BtnStyle.xaml`：深色主题任务栏封面按钮改用深色悬停背景和白色前景，避免出现亮底暗图标。

## 2026-10-06 最终确定任务栏封面按钮明暗映射
- `Style/BtnStyle.xaml`：Light 主题使用暗色悬停背景与白色前景，Dark 主题使用亮色悬停背景与黑色前景。

## 2026-10-06 修正任务栏封面按钮明暗主题映射
- `Style/BtnStyle.xaml`：交换任务栏封面按钮 Light/Dark 主题的悬停、按下和前景画刷，深色主题改用暗色叠层。

## 2026-10-06 修正切歌按钮可用状态回显
- `Services/PlaybackCommands.cs`、`DesktopLyrics/DesktopLyricsWindow.xaml`、`View/MainPage.xaml`：新增可通知的 `CanSwitch` 状态，任务栏和 MainPage 的上一首/下一首按钮显式绑定共享状态，并与命令执行守卫保持一致。

## 2026-10-06 统一任务栏媒体按钮样式与切歌命令状态
- `Style/BtnStyle.xaml`：新增 `TaskbarCoverPlayPauseButtonStyle`，任务栏封面按钮悬停状态与普通菜单按钮采用相反明暗方向，并保留主题资源适配。
- `DesktopLyrics/DesktopLyricsWindow.xaml`、`.xaml.cs`：频谱画布统一为 40×32，柱条水平居中；任务栏上一首/下一首改由共享命令的 `CanExecute` 驱动。
- `View/MainPage.xaml`：上一首/下一首移除重复的列表启用条件，与任务栏复用相同命令可用状态。

## 2026-10-06 修复任务栏频谱位置和幅度
- `DesktopLyrics/DesktopLyricsWindow.xaml.cs`：任务栏频谱柱条改为贴画布底部向上绘制，动态幅度加倍，恢复正确的底部视觉位置。

## 2026-10-06 修复任务栏控件绑定、单声道频谱显示与 FFT 生命周期
- `DesktopLyrics/DesktopLyricsWindow.xaml`、`.xaml.cs`：任务栏窗口显式初始化和关闭 `x:Bind` 跟踪，恢复播放、切歌按钮命令、音量双向滑块和图标状态；音量图标改为纯状态图标，不再额外弹出按钮。
- `DesktopLyrics/DesktopLyricsWindow.xaml.cs`：任务栏仅将双声道 FFT 快照合并为单声道频带绘制，并固定在中线以上显示，保留传输层双声道数据；封面播放按钮保持透明命中区域，仅悬停显示图标。
- `External/AudioPlayer/Playback/FftAnalyzer.cs`：FFT 工作数组和唤醒句柄改为启用时创建、停用时等待工作线程退出后释放；关闭桌面歌词或切换悬浮窗后不再持续生成 FFT。
- `External/BassPlayerIpc.Shared/PipeStateServer.cs`、`PipeStateClient.cs`、`Services/IpcService.cs`：FFT 管道缓存改为按需分配，停用时清除最新快照和大帧缓冲。
- `_tools/PlaybackSwitchRegression/FftTests.cs`、`_tools/AudioPlayerSmokeTest/Program.cs`：增加 FFT 停用释放、发布者收尾和真实管道启停回归入口。
- `AGENTS.md`：补充跨页面 MVVM、非激活窗口绑定初始化、AudioPlayer 发布和任务栏控件风格约定。
- 验证：共享协议、AudioPlayer 构建、AudioPlayer NativeAOT 发布和 FFT 生命周期回归通过；当前环境主 WinUI 构建未能完成 ResolvePackageAssets/MSIX 阶段，需在目标 Windows 环境复核。

## 2026-10-06 按 MainPage 统一任务栏音量交互并同步播放端
- `DesktopLyrics/DesktopLyricsWindow.xaml`、`.xaml.cs`：任务栏播放、上一首、下一首和音量控件改为命令/绑定驱动；音量滑杆复用 MainPage 的 `TwoWay` MVVM 绑定、静音/恢复命令和滚轮调节行为；歌曲信息悬停控制条固定为与封面相同的高度。
- `Player/AudioPlayer.exe`：发布包含 FFT 命令与双声道 FFT 输出的 NativeAOT 播放端，避免运行时继续使用旧播放进程导致频谱无数据。
- 验证：AudioPlayer NativeAOT 发布、XAML 结构与事件处理器静态检查通过；主 WinUI 构建仍受当前 SDK Workload resolver 环境限制。

## 2026-10-06 修复任务栏媒体控件交互与频谱刷新
- `DesktopLyrics/DesktopLyricsWindow.xaml`、`.xaml.cs`：封面播放按钮改为封面同尺寸、默认隐藏、移入显示且全状态透明；音量滑杆复用 `AppViewModel.Volume` 的 MVVM 双向绑定与现有播放器音量命令，并按实际状态回显。
- `DesktopLyrics/DesktopLyricsWindow.xaml.cs`：任务栏频谱在任务栏可见且选中本地曲目时保持请求，暂停时不产生新的 PCM FFT 帧；隐藏或关闭任务栏歌词时停止请求和界面刷新计时器。
- 验证：XAML 事件处理器与结构静态检查、共享协议/AudioPlayer/回归工具构建及 FFT 回归通过；主 WinUI 构建仍受当前 SDK Workload resolver 环境限制。

## 2026-10-06 重设计桌面歌词任务栏媒体控制布局
- `DesktopLyrics/DesktopLyricsWindow.xaml`、`.xaml.cs`：播放/暂停按钮集成到封面，上一首/下一首与音量滑杆在歌曲信息悬停时显示，原控制按钮区域改为双声道频谱可视化。
- `DesktopLyrics/TaskbarDesktopLyricsHost.cs`：同步任务栏媒体列宽度与新的封面、歌曲信息、频谱布局。
- 频谱效果在任务栏歌词可见且选中本地曲目时请求 FFT；暂停时不产生新的 PCM 帧，隐藏或关闭任务栏歌词时自动停用。
- 验证：XAML XML 结构检查通过；主 WinUI 构建受当前 SDK Workload resolver 环境限制，待 Windows 实机验收任务栏、DPI 与悬停交互。

## 2026-10-06 增加 AudioPlayer 双声道 FFT 数据通道
- `AudioPlayer`：从最终 PCM 渲染块异步计算双声道 FFT；DoP、Native DSD 和 Atmos IEC 61937 直通标记为不可用，不影响音频渲染线程。
- `BassPlayerIpc.Shared`、`IpcService`：复用状态管道传输可丢弃的最新 FFT 快照，并提供未来频谱效果可调用的启停接口。
- `_tools/PlaybackSwitchRegression/FftTests.cs`：增加双声道频点、元数据和关闭状态回归验证。
- 验证：共享协议、AudioPlayer、PlaybackSwitchRegression 构建通过；FFT 回归通过。

## 2026-10-06 修复普通歌词切换后点击跳转失效
- `Controls/Lyrics/LyricsControl.xaml`、`Controls/Lyrics/LyricsControl.xaml.cs`：按 `SimpleLyricsControl` 实例的加载/卸载重新绑定点击事件，避免切换高级逐字歌词后普通歌词重建时丢失跳转事件。

## 2026-10-06 改为复用 Win2D 画布并按激活状态惰性创建资源
- `PlayingDetailPage`：`NowPlayingCanvas`、动画文本和歌词宿主按需首次激活创建，后续通过激活属性暂停/隐藏渲染，避免开关设置时反复创建 `CanvasAnimatedControl`。
- `LyricsControl`、`AdvanceLyricsCanvasControl`：歌词宿主与高级歌词画布改为单实例，停用时隐藏并解绑总线和共享动画时钟，重新启用时恢复。
- `NowPlayingCanvas`、`AnimatedTextBlock`：背景着色器、图片解码、文本布局和画刷在控件首次激活且设备资源就绪后创建，真正关闭宿主时统一释放。
- `NowPlayingCanvas`：统一背景绘制、更新、替换和释放的生命周期锁，避免渲染回调持有旧原生对象时并发释放。
- 验证：AnimatedWin2dControls 项目构建通过（0 个错误）；待使用新构建运行进程复测工作集、私有内存、句柄和线程基线。

## 2026-10-06 修复着色器背景反复切换导致内存持续上升
- `ImageBackgroundRenderer`：将图片解码收敛为单一可取消后台循环，切换或卸载时取消在途 WinRT 操作并在其结束后释放同步资源，避免旧解码任务长期持有像素缓冲区。
- `ImageBackgroundRenderer`、`NowPlayingCanvas`：为图片位图和效果的绘制、设备重建与释放建立互斥生命周期，避免交换链切换期间并发释放原生资源。
- `NowPlayingCanvas`：复用图片背景和旋转网格渲染器，避免 `D2D1ResourceTextureManager` 仅能依靠终结器回收时因反复创建造成原生资源堆积；背景停用时停止解码，宿主关闭时统一释放缓存实例。
- 验证：`dotnet build WinUIMusicPlayer.csproj --no-restore -c Debug -p:Platform=x64`（0 个错误）。

## 2026-10-06 修复 Win2D 动画文本块开关与资源释放
- `PlayingDetailPage`：动画文本块的 `x:Load` 与普通文本替代视图的可见性改为 `OneWay`，开关切换即时更新；动态创建后重新应用当前文字特效。
- `AnimatedTextBlock`：卸载时解除画布事件、移除 Win2D 画布并释放文本布局、画刷和格式资源，避免反复切换留下原生资源。
- `CoverBackgroundSettingsControl`：移除动画文本与着色器背景开关已过时的“重启应用生效”说明。

## 2026-10-06 修复着色器切换与高级歌词即时生效
- `NowPlayingCanvas`、`PlayingDetailPage`：交换链重建或动态加载后重新应用当前曲目的调色板和封面，避免切换着色器后首曲目颜色/图像失真。
- `NowPlayingCanvas`、`AdvanceLyricsCanvasControl`、`LyricsRenderCoordinator`、`SimpleLyricsControl`：将 x:Load 卸载改为可复用的资源释放，保留最终关闭路径，避免重载后状态和原生资源失配。
- `LyricsControl`：高级逐字歌词与普通歌词的 x:Load 绑定统一为 OneWay，关闭背景图片时切换高级歌词立即更新。
- `LyricsSettingsControl`、`SettingsDialog`：移除“重启应用生效”提示。

## 2026-10-06 修复图片背景与高级逐字歌词的交换链冲突
- `PlayingDetailPage`、`NowPlayingCanvas`：保留 `MainWindow` 图片层持续渲染，补齐动态 Win2D 画布的歌词区域初始化，并统一无图模式的 OneWay 切换与资源释放。
- `NowPlayingCanvas`、`ImageBackgroundRenderer`：将图片背景作为全窗口背景渲染器，与高级歌词在同一个 `CanvasAnimatedControl` 交换链中绘制，避免把整窗图片错误裁剪到歌词区域。
- `PlayingDetailPage`、`BindUtils`：关闭着色器且启用高级逐字歌词并设置图片时，启用统一画布绘制图片与歌词；`MainWindow` 图片层持续保留，由统一画布覆盖详情页内容。
- `Strings/*/Resources.resw`：补充图片背景与高级逐字歌词组合时的行为说明。
- `CustomMicaSystemBackdrop`、`CustomAcrylicSystemBackdrop`、`ThemeStyleHelper`：补充系统支持检查、主题/窗口事件解绑和激活状态同步，不支持时回退透明材质，避免切换主题或窗口重建后残留旧控制器。

## 2026-10-05 任务栏歌词媒体按钮恢复默认按钮视觉
- `DesktopLyricsWindow`：将悬浮按钮专用的 Button 主题资源覆写（深/浅底色与悬停前景）从窗口根 `Grid.Resources` 收窄到 `ControlPanel.Resources`，任务栏媒体按钮不再继承覆写，恢复系统默认悬停/按下样式。

## 2026-10-04 重组桌面歌词自动隐藏设置并修复任务栏锁定图标
- `LyricsSettingsControl`：将桌面歌词自动隐藏改为 `SettingsExpander`，分别承载悬浮和任务栏模式两个 `SettingsCard`。
- `DesktopLyricsWindow`：任务栏子窗口不再依赖未触发的窗口级 `x:Bind` 初始化锁定 Glyph，改为随锁定状态显式刷新图标。

## 2026-10-04 分离桌面歌词的翻译与发音设置
- `LyricsPreferencesState`、`AppViewModel`、`DesktopLyricsViewModel`：新增独立的桌面歌词发音开关，桌面歌词翻译和发音只消费桌面歌词设置，普通歌词设置不再串联影响。
- `LyricsSettingsControl`、各语言 `Resources.resw`：新增桌面歌词发音显示开关。

## 2026-10-04 固定任务栏媒体区并支持自定义歌词宽度
- `DesktopLyricsWindow`、`LyricsSettingsControl`：固定任务栏左侧封面和媒体控件列宽，新增任务栏歌词宽度 NumberBox（240–2400 像素）。
- `TaskbarDesktopLyricsHost`、设置持久化：按歌词宽度计算默认任务栏边界，设置修改后立即调整已打开的任务栏歌词并保留用户解锁后的布局。
- 各语言 `Resources.resw`：补充任务栏歌词宽度设置的标题和说明。

## 2026-10-04 修复任务栏歌词交互与分模式显示
- `DesktopLyricsWindow`：任务栏模式改用静态歌词渲染，补齐封面异步加载，保持透明背景和紧凑高度；翻译／发音开关实时刷新并按行数压缩字号。
- `DesktopLyricsWindow`、`TaskbarDesktopLyricsHost`：播放控件显式转发到共享 `PlaybackCommands`，任务栏模式解锁后支持拖动、边缘缩放和相对位置/尺寸持久化，兼容任务栏居中布局。
- `DesktopLyricsViewModel`、设置持久化与播放详情设置页：悬浮和任务栏模式分别保存“播放详情页隐藏桌面歌词”开关，旧版单开关设置首次读取时迁移到两种模式。

## 2026-10-04 修复任务栏播放控件导致启动失败
- `DesktopLyricsWindow`：移除会被 WinUI 当作普通属性解析的 `x:Uid` 提示绑定，改为初始化后设置本地化 `ToolTipService.ToolTip`，避免创建桌面歌词窗口时崩溃。
- 各语言 `Resources.resw`：将播放控件提示改为无属性后缀资源键，并完成全语言校验。

## 2026-10-04 完善任务栏歌词媒体控件
- `DesktopLyricsWindow`：任务栏模式新增封面、曲名/艺术家/专辑信息和上一首／播放暂停／下一首控制组，复用现有封面加载队列与 `PlaybackCommands`。
- `DesktopLyricsWindow`：根据翻译、发音开关的实际行数自动压缩任务栏歌词字号，确保辅助歌词仍能同时显示。
- `TaskbarDesktopLyricsHost`：取消整窗鼠标穿透，保留歌词托管同时允许任务栏媒体控制交互。
- 各语言 `Resources.resw`：补充任务栏播放控件的本地化提示文本。

## 2026-10-04 重构桌面歌词宿主并接入任务栏模式

- `DesktopLyrics`：新增宿主接口与悬浮／任务栏模式，任务栏模式复用现有歌词渲染器并跟随 Explorer 重建；为后续壁纸宿主保留模式契约，不引入频谱和系统指标模块。
- `TrayViewModel`、`NotifyIconControl`、各语言资源：托盘菜单新增桌面歌词模式切换，并持久化当前模式。

## 2026-10-04 修复桌面歌词未接入发音轨

- `DesktopLyricsStyle`、桌面歌词两个渲染器：接入现有发音歌词快照与显示开关，桌面歌词按“发音、原文、翻译”顺序渲染，并随播放页发音开关即时更新。

## 2026-10-03 修复播放页翻译/发音切换时歌词滚动晃动

- `LyricsRenderCoordinator`：副行翻译／发音展开或收起期间锁定当前歌词的视口锚点，避免布局重算与滚动缓动同时追逐造成上下晃动；用户手动滚动时保留原有行为。
- `_tools/LyricsRenderRegression/MotionChecks.cs`：增加切换副行期间当前歌词锚点偏移回归断言。

## 2026-10-03 修复混合语言歌词开关按行生效

- `LyricsRomanizer`、`LyricsRefreshService`：发音生成和已有发音轨展示均按每行语言开关过滤，混合歌词不再被整首歌词的语言设置错误地短路或放行。
- `_tools/LyricsUnificationRegression/Program.cs`：增加生成发音轨与已有发音轨的混合语言开关回归用例。

## 2026-10-03 修复高级逐字歌词副行收起后透明度未重算

- `RenderLyricsLine`、`LyricsRenderCoordinator`、`LyricsAnimator`：副行翻译/发音的显示过渡期间持续按最新行位置重算透明度距离，副行收起后新进入可视区域的非当前行不再保持错误的透明状态。
- `_tools/LyricsRenderRegression/MotionChecks.cs`：增加翻译和发音同时关闭后可视行数量及非当前行透明度的回归断言。

## 2026-10-03 修复高级逐字歌词重绘后浮动效果丢失

- `RenderLyricsLine`、`LyricsLayoutManager`、`LyricsRenderCoordinator`、`CanvasLyricsRenderer`：重建逐字布局时按字符索引保留启用中的浮动过渡状态；窗口尺寸或高级歌词设置触发重绘后，当前字形继续保持浮动，关闭浮动时仍从零开始。
- `_tools/LyricsRenderRegression/FlowWaveWordExitChecks.cs`：增加播放中重排后浮动偏移保持的回归断言。

## 2026-10-03 混合语言歌词按行选择发音引擎

- `LyricsRomanizer`：整首语言仅作为无标记汉字行的默认值；每行独立识别假名、韩文和汉字，英语、数字、标点行不再生成重复发音，混合非拉丁脚本的单行交由用户发音轨处理。
- `_tools/LyricsUnificationRegression/Program.cs`：增加中、日、韩与拉丁文字混搭的音译回归用例。

## 2026-10-03 移植本地歌词音译引擎

- `LyricsRomanizer`：移植 lyric-romanizer 的整首脚本路由思路，接入本地普通话、粤语、日语和韩语音译；日语使用 IPADIC 读取汉字，缺少词典时保留可用的假名转换。
- `LyricsRefreshService`：用户发音轨优先；缺失时仅为当前显示快照生成发音，不写入数据库、一次性缓存或侧车文件。
- `WinUIMusicPlayer.csproj`、回归工具和第三方依赖说明：加入本地音译所需的 csharp-pinyin、csharp-kana 与 LibNMeCab/IPADIC。

## 2026-10-03 标签不明确时按歌词脚本判断发音语言

- `LyricsLanguagePolicy`、`LyricsRefreshService`：明确语言标签优先；缺少或不支持标签时按假名、韩文和汉字脚本判断，汉字无法确认粤语时默认普通话，只有 `yue` 标签启用粤语发音。
- `LyricsSettingsControl`、各语言资源：更新发音语言设置说明。

## 2026-10-03 按明确歌词语言控制用户发音轨

- `LyricsSettingsControl`、偏好状态与设置持久化：增加普通话、粤语、日语、韩语发音开关。
- `LyricsLanguagePolicy`、`LyricsRefreshService`：仅使用 TTML 明确语言标签；无标签或泛化 `zh` 不识别、不自动补取，避免把普通话和粤语混淆。
- `LrcService`、`LyricsOnlineSearch`、`LyricsRefreshService`：移除网易云在线音译和自动补取；发音只来自用户提供的 TTML 或独立发音 LRC，自动生成的预览结果不写入数据库。

## 2026-10-03 完善发音歌词分层与 TTML 导入

- `RenderLyricsLine`、`RenderLyricsAuxiliaryLayer`、`LyricsLayoutManager`：发音、原文、译文拆为独立渲染层，发音位于原文上方并按原文字号的 60% 显示，切换时同步过渡副行高度。
- `SimpleLyricsControl`：普通歌词按发音、原文、译文顺序布局，译文和发音隐藏时动画收缩行高。
- `LyricsParser.Ttml`：TTML transliteration 元数据和正文 `x-roman` 辅助轨独立导入 `PronunciationLrc`，即使存在外部译文也保留，并按源语言筛选发音轨。
- `CanvasLyricsRenderer`：桌面歌词的文本边界和阴影包含独立发音层。

## 2026-10-02 修复播放页翻译/发音开关文字垂直不居中

- `PlayingDetailPage`：译/音 两个文字开关按钮的 TextBlock 加 `TextLineBounds="Tight"`，行框改按字墨迹计算，ContentPresenter 居中的即为墨迹本身；修复 CJK 字形因行框上方空隙在按钮内偏低约 2px、与旁边齿轮/时钟图标不对齐的问题（实测与图标中心偏差从约 2 逻辑像素降到 1 物理像素内），对其他语言的 T/A 字母同样适用。

## 2026-10-02 增加发音歌词轨与显示开关

- `LyricsDocument`、歌词解析／存储／文件读取：支持独立的 `_Pronunciation.lrc` 逐行 LRC，按时间戳与原文对齐，并为旧数据库追加可空发音列。
- `MusicDetailsWindow`、普通／高级歌词控件：新增发音编辑页签和发音显示，翻译与发音可分别隐藏。
- `LyricsSettingsControl`、当前播放页底部播放栏：新增翻译／发音开关，状态持久化并即时同步到歌词渲染器；主界面播放栏不显示无歌词上下文的按钮。
- TTML transliteration 元数据：按正文行时间导入 `PronunciationLrc`；播放栏仅在当前播放页保留开关，普通／高级歌词轨道增加淡入淡出，发音字号为原文 60%。
- 普通与高级歌词切换翻译／发音时同步动画收缩或展开副行高度，后续歌词行随布局平滑移动，不再只淡出文字而保留空行。
- 修复高级歌词副行高度动画期间沿用旧 Win2D 文本缓存坐标导致主歌词被裁掉或只剩空白背景的问题；改用行级平移保持缓存稳定。
- `LyricScrollMotion`：FlowWave 保留未启动行的错峰等待；已有运动的行在重新排队时继续当前运动，避免错峰目标替换造成确定性停顿。

## 2026-10-01 优化 AnimatedTextBlock 循环间距分配

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：每个文本段布局准备只测量一次空格宽度，移除每行的拼接字符串和字符区域数组分配。

## 2026-10-01 修复 AnimatedTextBlock 循环间距未生效

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：显式使用尾部空格的布局宽度计算循环距离，修复重复文本仍然贴连的问题。

## 2026-10-01 调整 AnimatedTextBlock 循环间距

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：循环副本之间增加一个空格宽度，避免首尾文字贴连。

## 2026-10-01 改为 AnimatedTextBlock 单向循环滚动

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：悬停文字改为单向首尾相接滚动，使用相邻文本副本消除循环边界空白和反向回弹。
- `_tools/AnimatedTextRegression`：增加单向运动和完整文本宽度循环回归检查。

## 2026-10-01 修复 AnimatedTextBlock 悬停滚动启动延迟

- `External/AnimatedWin2dControls/.../AnimatedTextBlock`：鼠标进入后立即开始首段滚动，保留到达两端后的短暂停留。
- `_tools/AnimatedTextRegression`：增加悬停进入后首个时钟帧的即时滚动回归检查。

## 2026-10-01 修复播放页无歌词占位符缺失

- `LyricsLoader`：无歌词时生成一行仅用于展示的提示歌词，复用普通与高级歌词行的字体、字号和渲染效果。
- `LyricsPresentationService`：同步空歌词快照，避免新建歌词控件保留旧内容。

## 2026-10-01 清理歌曲属性中的大模型状态控件

- `MusicDetailsWindow.xaml`：移除翻译状态区域的固定高度容器和进度环，恢复歌词导出结果与编辑错误的原有文本布局。

## 2026-10-01 移除未发布的大模型歌词翻译界面

- `LyricsSettingsControl`、`MusicDetailsWindow`：移除设置入口、歌曲属性手动翻译按钮及相关对话框。
- `LlmTranslationService`：保留服务层总开关，确保旧配置不会触发 API 请求；默认配置继续保持关闭。

## 2026-10-01 修复手动大模型翻译后的保存状态

- `LyricsEditorViewModel`、`MusicDetailsWindow`：提交成功后不再因延迟绑定通知误报冲突；大模型翻译期间禁用保存操作，并显示可能耗时较长的提示。
- `LyricsEditorViewModel`：翻译期间若用户修改原文，丢弃过期结果，避免覆盖用户草稿。
- `MusicDetailsWindow`：将手动大模型翻译按钮移入歌词操作按钮组，与刷新按钮放在同一处。

## 2026-10-01 恢复歌词空状态提示

- `LyricsControl`：将“暂无歌词，请欣赏音乐”提示放在歌词容器根层，兼容普通和高级歌词渲染路径，并在歌词加载后自动隐藏。

## 2026-10-01 增加手动大模型歌词翻译

- `MusicDetailsWindow`、`LyricsEditorViewModel`：增加单图标手动翻译按钮，结果回填当前歌词草稿并复用现有保存流程。
- `LlmTranslationService`：自动和手动翻译统一记录一条 `Information` 触发日志；手动翻译允许覆盖译文并立即重试。

## 2026-10-01 自动识别大模型协议

- `LlmSettingsDialog`、`LlmSettingsViewModel`：移除协议下拉框，依据 API 地址自动选择 Anthropic 或 OpenAI 兼容协议。
- `LlmTranslationService`：保存和请求前重新归一化旧配置，避免 `/anthropic` 地址继续走 OpenAI 路径。

## 2026-10-01 修复大模型请求体序列化失败

- `Model/LlmSettings.cs`、`LlmTranslationService`：OpenAI 与 Anthropic 请求改用 System.Text.Json 源生成 DTO，兼容发布版禁用反射序列化的配置。

## 2026-10-01 修复大模型配置对话框和 Anthropic 兼容端点

- `LlmTranslationService`：Anthropic 兼容地址自动补齐 `/v1`，改进错误信息、空配置归一化，并将原文规范化为 LRC 后再翻译。
- `LyricsSettingsControl`、`LlmSettingsDialog`：设置页改为独立对话框，移除不可用的厂商模型列表请求，模型改为手工填写；保留清除系统密钥入口。
- `App.xaml.cs`：注册 `ILlmTranslationService`，使播放时自动翻译链实际接入服务。

## 2026-10-01 接入大模型歌词翻译

- `LlmTranslationService`、`Llm.json`：新增 OpenAI 兼容与 Anthropic 协议配置、思考强度和目标语言；API 密钥仅保存到 Windows PasswordVault，并提供清除密钥操作。
- `LyricsRefreshService`、`LyricsExporter`：播放加载歌词时自动翻译缺失的 LRC，先发布原文，成功后按数据库 revision 写回并生成 `_Translated.lrc` 侧车文件。
- `LyricsSettingsControl`、`LlmSettingsViewModel`、`Strings/*/Resources.resw`：新增歌词翻译设置界面。

## 2026-10-01 修复歌词边界进度和播放列表排序保存

- `BaseRenderLyrics.cs`：零时长逐字字符在起点边界返回确定的 0/1 进度，避免 NaN 进入 Win2D 裁剪。
- `PlayListViewModel.cs`、`PlayListPage.xaml`：连续拖拽排序合并并保存最后一次顺序，保存或其他歌单操作进行中时禁用继续拖拽。
- `_tools/LyricsRenderRegression`：补充零时长字符边界回归。

## 2026-10-01 对齐播放列表操作栏

- `PlayListPage.xaml`、`PlayListViewModel`：将编辑模式按钮和空状态可见性改为绑定状态，并统一操作栏按钮高度。
- `ToolUtils`、`AlbumPage`、`ArtistPage`、`FolderBrowsePage`、`PlayListPage`：复用带条件匹配的公共视觉树查询，移除页面内重复实现。

## 2026-10-01 调整播放列表悬浮操作

- `PlayListPage.xaml.cs`：编辑模式隐藏卡片上的全部悬浮按钮，普通模式恢复播放和更多按钮。

## 2026-10-01 修复播放列表页导航崩溃

- `PlayListPage.xaml.cs`：避免在 `SelectionMode=None` 时清空 `GridView.SelectedItems`，修复首次打开播放列表页触发 `0x8000FFFF` 的问题。

## 2026-10-01 播放列表编辑模式与批量管理

- `PlayListPage`、`PlayListViewModel`：新增编辑模式，保留悬浮播放按钮；编辑模式下支持多选、全选、批量删除和拖拽排序，普通模式点击卡片仍进入歌单详情。
- `PlayList`、`MusicDatabaseService`：为播放列表增加持久化排序字段，兼容已有数据库并以事务保存批量删除与排序结果。
- `Strings/*/Resources.resw`：补充编辑模式、批量删除和二次确认文本。

## 2026-10-01 修复发光退场后末字再次骤暗

- `LyricsAnimator.cs`、`RenderLyricsChar.cs`、`LyricsLineRenderer.cs`：记录视觉退场开始时的实际发光量，退场时同步降低发光层透明度与模糊半径，避免半径归零时叠加一份普通高亮字形，再切换为非当前行而骤暗；沿用已有曲线、逐字时间及退场状态清理。
- `_tools/LyricsRenderRegression`：加入完整《Paint It Black》旁挂夹具，真实 Win2D 对六处 black 的末字和前字做交接帧像素检查，覆盖明／暗文字及 4／8ms 采样；首次复现末字 alpha 总量下降约 48%，前字约 1%。
- 验证：六处交接像素及既有原生回归、主程序 Debug x64 构建通过（0 错误）；固定采样下首处末字变化由 48.13% 降至约 0.93%。完整播放器随真实音频的人工验收尚未执行。

## 2026-09-30 修复流波切句时末字重音突变

- `LyricsAnimator.cs`、`RenderLyricsLine.cs`：流波末尾长音节在已有 300ms 留白内保持视觉重音，切句后从当前状态沿同一行的传播延迟及退场时长回落；音乐索引、逐字填充与源时间不延长，真实长空档正常收尾，跳转／重排清理残留状态。
- `LyricsRenderCoordinator.cs`、`LyricsLineRenderer.cs`：独立保存视觉退场状态，末字退出完成前继续逐字绘制并随行模糊，复用现有原生效果，避免先变成普通高亮再切行；既有流波曲线和位移时序不改动。
- `_tools/LyricsRenderRegression/FlowWaveWordExitChecks.cs`：真实 Win2D 覆盖无留白／300ms 留白、长空档、共享退场、暂停、双向跳转、重排及完整样例 brave；常规样式提前收尾、桌面高光、短句与资源生命周期回归继续通过。
- 验证：修复前原生复现发光 4.999901→0、旧行全亮静止并等待 143ms；修复后原生回归、BetterLyrics 运动基线与 Debug x64 构建通过（0 错误）；完整播放器随真实音频的人工观感验收尚未执行。

## 2026-09-30 统一歌词优先级卡片布局

- `LyricsSettingsControl.xaml`：移除文件／来源优先级下拉框上方的序号标题，控件垂直居中并以箭头表示从左到右的优先顺序，消除标题造成的额外卡片高度；保留无障碍名称与原有交换逻辑。
- 验证：刷新生产 XAML 夹具后，真实 WinUI 编译绑定、顺序恢复／交换回归及主程序 Debug x64 构建通过（0 错误）。

## 2026-09-30 跳过逐字歌词中的空字与空行

- `LyricsParser.cs`、`LyricsParser.Ttml.cs`：忽略 QRC／KRC 空字标签和各逐字格式整行纯空白内容，避免空行触发切句或占用来源优先级；保留有效正文中的空格、词间停顿及有正文的零时长字，原文不改写。
- `_tools/LyricsUnificationRegression`、`LyricsRenderRegression`：覆盖重复空时间戳、逐字中间空字组、纯空白行、来源回退及真实 Win2D 切句／暂停／回拖，普通 LRC 空行继续沿用旧版忽略行为。
- 验证：修复前 13 项空字／空行断言失败；修复后 245 项核心回归、原生渲染回归及 Debug x64 构建通过（0 错误）；完整播放器随真实音频的人工验收尚未执行。

## 2026-09-30 恢复逐字歌词重构前的播放计时

- `LyricsRefreshService.cs`：恢复已有格式统一的 300ms 逐字提前量、分词音节边界、展示补尾和未知时长兜底，为末字重音动画保留回落时间；TTML 保留显式时间与重叠，源文件及数据库原文不改写。
- `LyricsParser.cs`：增强 LRC 缺少闭合标签的末字恢复零时长，避免补到下一句后改变原有播放节奏。
- `_tools/LyricsUnificationRegression`、`LyricsRenderRegression`：加入完整《The Story of Us》及执行重构前代码得到的 62 行冻结基线，真实 Win2D 验证 brave 在 157681ms 切句前完成填充、缩放／发光／字浮回落和回拖。
- `docs/LyricsRefactorBehaviorAudit.md`、`LyricsUnificationPlan.md`：逐项记录重构前后播放、来源、翻译、迁移、保存、导出、统计及生命周期差异，明确旧格式展示兼容与 TTML 源时间的边界。
- 验证：修复前 10 项播放兼容断言失败；修复后 202 项核心、29 项提供方／加载／封面回归、主界面与桌面原生回归及 Debug x64 构建通过（0 错误）；完整播放器随真实音频的人工验收尚未执行。

## 2026-09-30 歌词文件与来源优先级组合设置

- `LyricsSettingsControl.xaml`、`SettingsViewModel.cs`：文件顺序改为四个优先级下拉框，选择已占用格式时交换两项；新增文件／数据库的两个来源优先级下拉框，统一由偏好状态驱动。
- `LyricsPreferencesState.cs`、`PreferencesState.cs`、`AppSettings.cs`、`SaveSettings.cs`、设置保存／恢复服务：持久化来源顺序，保留旧文件排序及默认文件优先行为。
- `LyricsRefreshService.cs`：按来源顺序使用有效歌词，无效或缺失时回退；数据库优先命中时跳过本地／WebDAV 文件读取，一次性播放使用已保存缓存；保留用户清空与在线搜索取消规则。
- 七种语言资源、`_tools/LyricsUiRegression`、`LyricsUnificationRegression`、`SettingsPersistenceRegression`：补充本地化、编译绑定、实际文件／SQLite 回退和设置兼容性检查。
- 验证：120 项歌词核心回归、真实 WinUI 下拉框绑定／交换、设置 JSON 往返、七种语言资源检查及 Debug x64 构建通过（0 错误）；完整播放器设置页目视验收和真实 WebDAV 服务器验证尚未执行。

## 2026-09-30 修复句间空档中歌词高光提前消失

- `LyricsRefreshService.cs`、`LyricLine.cs`：派生独立的展示高光终点，空档保持到下一次入句，末行保持到歌曲结束；原文、实际行尾及逐字时间不变，保留同起点多行和合法重叠。
- `RenderLyricsLine.cs`、`LyricsAnimator.cs`、`LyricsSynchronizer.cs`、`LyricsRenderCoordinator.cs`、`CanvasLyricsRenderer.cs`：主界面和桌面歌词统一使用展示边界控制高光，修复空档内回拖仍保留其他行的问题；逐字进度沿用真实时间，短句流波处理不变。
- `_tools/LyricsUnificationRegression`、`LyricsRenderRegression`：覆盖真实文件／SQLite 迁移投影和《Unchained》长空档，真实 Win2D 像素及桌面窗口验证切句、暂停、拖动和重叠。
- 验证：修复前 83.675 秒的原生高光断言失败；修复后 92 项歌词回归、主界面／桌面原生渲染、流波／短句回归和 Debug x64 构建通过（0 错误）；完整播放器随音频的人工验收尚未执行。

## 2026-09-30 逐字歌词流波节奏对齐 BetterLyrics

- `LyricScrollMotion.cs`、`LyricScrollTiming.cs`、`LyricsRenderCoordinator.cs`：流波从可见区顶部逐行传播，等待新目标时保持位置并保存速度，起动延迟重新计时，对齐 BetterLyrics `dad48ab9`；保留短句削弱／关闭错峰和稳定弹簧响应。
- `LyricsAnimator.cs`、`ValueTransition.cs`：位移、缩放、模糊和透明度共用逐行时序，修正延迟帧位置及多段索引，避免初始透明行反复重启延迟；桌面单行歌词沿用原路径。
- `_tools/LyricsEasingRegression`、`LyricsRenderRegression`：覆盖延迟接续、首段等待、多段过渡、真实 Win2D 逐帧效果同步、短句与跳转／重排。
- 验证：与 `dad48ab9` 的真实运动类数值对照、CPU 回归、真实 Win2D 效果同步／生命周期回归及 Debug x64 构建通过（0 错误）；完整播放器的人工观感对比尚未执行。

## 2026-09-30 修复 CR 换行的 LRC 被合并为一行

- `Services/Lyrics/LyricsParser.cs`：格式检测与解析统一识别 CR、LF、CRLF；修复 CR 下的 offset 和 KRC 翻译元数据移除，旧缓存直接重新解析，LRC 原文保持不变。
- `_tools/LyricsUnificationRegression/LineEndingChecks.cs`：以完整 65 行样例覆盖四种换行、翻译与导出往返，验证真实本地歌词文件、JSON 缓存和 SQLite 旧文档的播放展示投影。
- 验证：修复前样例仅解析为 1 行，修复后 78 项回归及 Debug x64 构建通过（0 错误，1738 个警告）；两个历史 TTML 外部附件未提供，跳过其 4 项检查，完整播放详情页尚未实机目视验收。

## 2026-09-30 修复歌词审查发现的保存、恢复与清空问题

- `LyricsEditorViewModel.cs`、`LyricsRepository.cs`：元数据保存保留未修改的旧歌词、来源及诊断；恢复旧候选先显示原始文本，转换失败仍可编辑修复。
- `MusicDatabaseService.Metadata.cs`、`PendingMetadataWrite.cs`、`ToolUtils.cs`：无法解析且未修改的歌词不阻止标签写入，队列重启后仍保留文件已有歌词；显式清空继续清除标签。
- `LyricsCacheStore.cs`、`OneShotLyricsCache.cs`、`LyricsRefreshService.cs`、`OneShotPlaybackService.cs`：一次性缓存记录用户来源，清空后不再自动补回，入库后保留该意图；手动刷新与旧缓存兼容。
- `_tools/LyricsUiRegression`、`LyricsUnificationRegression`、`FolderScanRegression`：补充对应失败场景、真实 JSON／SQLite 往返与队列重启检查；`docs/LyricsUnificationPlan.md` 同步固定页签名称及修复边界。
- 验证：修复前新增 WinUI 场景稳定失败，修复后通过；49 项核心检查、扫描／标签队列回归、真实 ATL 标签保留／清空往返、七种语言资源检查与 Debug x64 构建通过（0 错误，389 个警告）。

## 2026-09-29 移除额外引入的压缩／加密歌词文件导入

- `LyricsFilePolicy.cs`、`WebDavLibraryService.cs`：移除 KRC 解密解压、十六进制 QRC 解密及自动识别入口，本地和 WebDAV 仅按文本读取歌词；在线提供方原有解码保持不变。
- `_tools/LyricsUnificationRegression`、`docs/LyricsUnificationPlan.md`：删除对应压缩／加密夹具和检查，更新功能范围说明。
- 验证：剩余 44 项歌词核心回归及 Debug x64 构建通过。

## 2026-09-29 修复快速切歌时歌词缓存释放竞态

- `LyricsLoader.cs`：恢复 500 ms 防抖，快速切歌仅加载最后一首；等待、在途请求和迟到缓存均受切歌／退出取消保护。
- `LyricsRenderCoordinator.cs`：UI 只提交最新托管快照，渲染帧边界统一替换歌词并回收旧 Win2D 缓存；绘制与关闭清理互斥，暂停重绘也能接收新歌词，清除旧歌曲的索引和滚动状态。
- `_tools/LyricsCoverRegression`、`_tools/LyricsRenderRegression`：补充防抖、迟到结果及真实 CanvasAnimatedControl 帧内资源存活、快速发布和关闭检查。
- 验证：修复前原生帧内缓存存活检查稳定失败；修复后 3 轮各 300 次发布、暂停重绘、空歌词和带待发布内容的关闭通过，加载／取消与滚动回归、Debug x64 构建通过。完整音频切歌及用户设备仍需实测。

## 2026-09-29 调整歌词编辑页签与恢复按钮布局

- `MusicDetailsWindow.xaml`、`LyricsEditorViewModel.cs`、七种语言资源：页签固定为“歌词／翻译”，移除标题的格式检测；空状态和错误消息折叠，恢复旧 LRC／KRC 与写入文件按钮放在同一底栏。
- 验证：Debug x64 构建、现有 WinUI 编译绑定与编辑冲突回归、七种语言资源键检查通过；完整音乐详情窗口的底栏尚未实机目视验收。

## 2026-09-29 统一歌词存储、迁移与 TTML 支持

- `Model/LyricsDocument.cs`、`Services/Lyrics/`：原文保留实际格式，翻译统一为独立逐行 LRC；不增加 Language、不推断同文件双轨 LRC。
- `LyricsRepository`、`MusicDatabaseService`、`OneShotLyricsCache`：新增版本化存储和条件保存；旧候选整对迁移，保留旧表、一致性备份与有界缓存恢复副本，支持后台续迁和编辑冲突诊断。
- `LyricsParser`、`LyricsRefreshService`、`WebDavLibraryService`：接入 issue #27 的行／词 TTML、独立译文和四种文件优先级；损坏文件回退，有效内容优先展示，自动搜词按提供方记录结果并可取消。
- `MusicDetailsWindow`、`LyricsEditorViewModel`、设置与七种语言资源：四个歌词内容区合并为两个，支持旧候选恢复和按格式导出；原文与 `_Translated.lrc` 分别保存，失败时回滚并保留备份。
- 扫描、标签队列、音频转换和 USB 导出统一使用新文档；标签与 USB 使用兼容 LRC，标签队列保留旧快照；播放次数移至已确认开始的播放路径，歌词重载不再计次。
- 验证：Debug x64 构建、47 项歌词核心检查、真实 SQLite／文件锁、网络取消／搜索状态、扫描／标签队列、播放导航／远程恢复、设置／共享状态回归通过；隔离 WinUI 编译绑定与 UI Automation 输入保存通过。TTML 附件实测 27 行原文／27 行翻译，逐词版保留 450 片段。
- 兼容性：默认顺序保持 KRC → QRC → LRC，并追加 TTML；旧版无法看到新版编辑，降级后旧行变化会报告冲突。未操作用户实际数据库；完整音乐库升级与真实设备播放仍需发布前验收。

## 2026-09-28 修复单曲播放列表曲终停播

- `PlaybackCommands.cs`、`BassPlayerCommandService.cs`、`PlaybackCoordinator.Gapless.cs`：列表循环和随机循环在队列仅有一首歌时允许自然结束后重新播放该曲，并为本地播放预载同一首歌；手动切歌及多曲候选规则保持原样。
- `_tools/PlaybackNavigationRegression/Program.cs`：覆盖两种模式的曲终续播、无缝切换和手动导航边界。
- 验证：播放导航及真实 WinUI Dispatcher 回归、Debug x64 与禁用 MSIX 打包的 Release x64 构建通过；标准 Release 打包因本机缺少 `mspdbcmf.exe` 未完成。

## 2026-09-27 关于页版权年份改为动态

- `AboutSettingsControl.xaml`、`AboutSettingsControl.xaml.cs`：两处 `© 2026 Sennpei Studio` 硬编码改为 `x:Bind` 函数绑定，运行时取 `DateTime.Now.Year`，跨年无需发版更新。

## 2026-09-27 合并关于页缓存设置并统计缓存总量

- `AboutSettingsControl.xaml`：WebDAV 播放缓存并入同一个“缓存”展开卡片，移除独立控件和重复路径；保留位置、播放缓存开关/上限及两种清理入口。
- `CacheSizeCalculator.cs`、`WebDavSourcesViewModel.cs`：汇总当前缓存位置的网络封面、封面子目录及 WebDAV 子目录（含未完成下载），按容量显示 B/KiB/MiB/GiB；后台串行统计，换目录丢弃旧结果，清理后刷新。
- `Strings/*/Resources.resw`：七种语言区分封面与播放缓存清理按钮；用户原始音乐文件不计入，已有清理范围保持不变。
- 验证：x64 构建、缓存计数/目录切换/清理刷新/退出回归及七种语言资源键静态检查通过。

## 2026-09-27 修复封面缓存失败时丢失可用封面

- `ImageSwitcher.xaml.cs`、`ImageHelper.cs`：展示缓存生成或解码失败时直接读取原图，按 EXIF 方向限制最长边 1536px，取消不触发兜底。
- `CoverPresentationService.cs`、`SystemMediaControlsService.cs`：展示缓存失败保留原图，文件不可用时使用已取得的封面字节；正常热缓存仍只传路径，兜底沿用切歌取消与退出屏障。
- `_tools/SmtcCoverRegression`、`_tools/SmtcCoverUiRegression`：增加缓存发布/替换失败、原图写入及打开失败、限尺寸解码、字节兜底、取消与退出回归。
- 验证：主项目 x64 构建、真实 WIC/SMTC 与 WinUI 控件/展示管线回归通过；未进行完整播放器及系统媒体面板的人工视觉验证。

## 2026-09-27 SMTC 与详情页共享高分辨率封面

- `PlaybackCoverImage.cs`、`ImageSwitcher.xaml.cs`、`ImageHelper.cs`：详情页与 SMTC 共用高分辨率封面文件，大图按比例限制最长边 1536px，小图不放大；串行生成并原子发布缓存，直接从文件流解码。
- `CoverPresentationService.cs`、`SystemMediaControlsService.cs`、`ReadOnlyMappedStream.cs`：热缓存避免读取原图大数组；SMTC 克隆共享文件页，UI 队列只保留路径，清空缓存不破坏在途读取，退出等待全部更新并释放流。
- `ToolUtils.cs`、`WebDavLibraryService.cs`：高分辨率缓存纳入启动保留、远程容量裁剪和清理。
- 验证：真实 WIC/SMTC 与 ImageSwitcher 回归通过，覆盖 JPEG 方向、PNG 透明度、取消、快速切歌、删除缓存和退出；1536px 噪声测试图热提交的托管分配约 7.42MB → 40KB/次，10 次文件流提交无 GC，仅代表此路径；完整系统媒体面板外观与整机 GC 停顿未测。

## 2026-09-27 修复 1.2.9.0 以来审查问题并优化无缝预载

- `PlaybackEngine.Gapless.cs`、`GaplessPreloader.cs`：按剩余实际播放时间 10 秒延迟创建待播会话，未知时长提前准备；单个工作任务合并过期计划，统一迟到会话与退出清理，PCM 环容量不变。
- `PlaybackEngine.cs`：普通 DSP、设备校正及试听更新当前与待播会话，避免重复销毁/解码/分配；倍速、seek、暂停、输出重建及关闭无缝仍使准备失效。
- `PlaybackQueueState.cs`、`PlaybackCoordinator.Gapless.cs`、IPC：队列版本缓存候选，已确认计划不轮询重发；提交失败退避，取消回执确认曲目身份后再替换，保护手动选曲及迟到通知。
- `FFmpegAudioConverter.cs`：重采样器重建失败释放新原生上下文；`WebDavRegression.csproj` 改用完整共享 IPC 项目，修复回归工具缺失依赖。
- `TempoProcessor.cs`：明确输入帧、输出帧及搜索窗口命名；`Player/AudioPlayer.exe` 同步 NativeAOT 产物，存量偏好保持不变。
- 验证：317/317 播放回归、导航/远程/WebDAV TLS 回归、真实 WinUI 调度及主程序构建；NativeAOT 在真实 WASAPI 共享输出通过 0.25× / 5× 无缝衔接、暂停恢复与退出。未实测 ASIO/独占硬件及完整主界面交互。

## 2026-09-27 修复展开图片背景设置时闪退

- `GeneralSettingsControl.xaml`：将背景错误 InfoBar 移到 SettingsExpander.Items 外，避免展开时应用 SettingsCard 样式导致 COMException；无错误时隐藏提示。
- `_tools/AppearanceUiRegression`：直接提取生产 XAML，在真实 WinUI/Toolkit 模板中复现原异常并验证展开、反复折叠/展开及错误提示显示/隐藏；主程序构建通过。

## 2026-09-27 主窗口图片背景与倍速默认选择

- `MainWindow.xaml`、`Controls/WindowBackgroundImage.cs`：新增铺满窗口的自定义图片背景与 0–100 模糊；按需读取、限制解码尺寸，换图/清除/退出释放合成资源，缺失或损坏图片回退原窗口材质，高对比度下隐藏图片。
- `GeneralSettingsControl.xaml`、`AppViewModel.WindowBackground.cs`、设置状态/保存链路、`Strings/*`：常规设置新增选择/清除图片和模糊滑块/数值输入；保存路径与模糊值，旧设置保持原背景，默认模糊 20；补齐七种语言独立资源键。
- `DspSettingsViewModel.Playback.cs`：绑定初始化直接读取已保存倍速，恢复偏好期间忽略临时选择写回；无设置时默认选中 1×，保留已有档位和旧自定义倍速。
- 验证：主程序构建、真实 WinUI 倍速绑定/图片合成与生命周期回归、设置持久化及七种语言资源检查；完整设置页、图片选择对话框与系统高对比度切换未做人工验证。

## 2026-09-27 固定倍速档位

- `DspSettingsControl.xaml`、`DspSettingsViewModel.Playback.cs`：倍速改为不可编辑下拉框，提供 0.25、0.5、0.75、1、1.5、2、3、4、5×；避免显示浮点尾数，旧自定义倍速保留至用户重新选择。
- `DspSettings.cs`、`PlaybackSnapshots.cs`、`RemotePlaybackService.cs`、`TempoProcessor.cs`：播放与遥测范围扩展为 0.25–5×，调整分析缓冲容量；同步七种语言说明及播放端产物。
- 验证：主程序与 NativeAOT 构建通过；九档真实解码时长、音调和 seek 重置通过；0.25× / 5× 的真实 WASAPI 无缝切歌、暂停恢复通过，七种语言取词键静态检查通过。设置页外观未做人工验证。

## 2026-09-27 无缝播放、保调倍速与动态压限

- `External/AudioPlayer/Playback`：预载下一首兼容本地 PCM，在同一输出回调内无停顿衔接；取消、暂停、seek、格式回退与退出保留明确的会话所有权。
- `Services/PlaybackCoordinator.Gapless.cs`、IPC：按真实队列及循环模式预载，带身份的切歌通知/进度同步曲目、歌词和统计；取消确认处理已经开始的切歌，手动选曲优先。
- `TempoProcessor.cs`、`DynamicsProcessor.cs`：加入 0.5–2× 保调倍速、声道联动软拐点压缩、补偿增益及 −1 dBFS 采样峰值限幅；进度与网络遥测按原曲时间计量。
- `DspSettingsViewModel`、`DspSettingsControl.xaml`、`Strings/*`：新增设置并保存偏好，补齐七种语言的独立资源键；旧配置默认 1×、压限关闭、无缝开启。不能 seek 的网络流与位流不启用倍速。
- `Player/AudioPlayer.exe`、共享协议：NativeAOT 产物同步更新；管道握手 v3，主程序与播放端须一同部署，DSP 设置兼容旧版本。
- 验证：313 项播放回归通过，另通过偏好持久化、远程恢复、真实 WinUI 调度及 NativeAOT + WASAPI 共享输出测试；九个新增取词键覆盖七种语言。尚未实测 ASIO/独占硬件及设置页视觉布局。

## 2026-09-27 AudioPlayer IPC 统一为持久 Named Pipe

- `External/BassPlayerIpc.Shared`：移除命令、进度、DSP 和设备校正的 MMF/信号量实现；统一版本化分帧、实例握手、有序确认、有界队列与取消/迟到响应的缓冲所有权。
- `External/AudioPlayer/PlayerIpcService.cs`、`StreamingServer.cs`、`Services/IpcService.cs`：常规命令与流媒体控制独立执行，进度/DSP 推送到本地缓存；关键通知保序，断开及退出等待在途 I/O 后释放资源。
- `External/BassPlayerIpc.Shared/Streaming.cs`、`Services/RemotePlaybackService.cs`：流媒体控制复用持久连接，分离状态查询、准备、seek/refresh 与播放控制；会话停止时关闭连接，不自动重放失败命令。
- `Player/AudioPlayer.exe`、播放端说明文档：更新 NativeAOT 分发产物；主程序与播放端必须同时更新，不兼容旧 MMF 传输，存量音频设置保持原格式。
- `_tools/PlaybackSwitchRegression`、`AudioPlayerSmokeTest`、`RemotePlaybackRegression`：迁移 IPC 用例并覆盖分帧、超时/取消、队列背压、跨进程快照与断开退出；305 项播放回归、远程恢复回归、真实 NativeAOT HTTP/DSF 集成及 WASAPI 共享输出冒烟通过。未执行 WinUI 交互、ASIO/独占硬件验证。

## 2026-09-26 旋转网格背景模糊随窗口尺寸缩放

- `External/AnimatedWin2dControls/AnimatedWin2dControls/Renderer/Background/RotatingMeshBackgroundRenderer.cs`：模糊半径按画布面积的平方根（几何平均边长）同比缩放，以 1920×1080 时的原有效果为基准，避免小窗口过度模糊；横竖屏同面积下模糊强度一致，按宽度缩放会导致竖屏相对失准。裁切、网格和其他渲染参数保持不变。

## 2026-09-26 修复 OpenList 网盘歌曲误报变化及保存来源卡顿

- `Services/WebDav/WebDavTransport.cs`、`RemoteResourceVersion.cs`、`HttpRangeReadStream.cs`、`RemoteReadSession.cs`：区分 DAV 目录与重定向下载资源的版本，修复播放、标签及封面读取误报“远程文件已变化”；在同一读取中继续校验下载 ETag、修改时间和长度，缺 ETag 不再误判 Range 不支持。
- `Services/WebDav/RemoteReadSession.cs`：下载版本无法对应目录强版本时停止自动缓存补全且不发布音频磁盘缓存，保留有界内存播放及定位，避免把直链数据错误归入目录版本缓存。
- `Services/WebDavLibraryService.Sources.cs`、`WebDavLibraryService.cs`：凭据和数据库保存移到后台；新增来源由扫描批次发布曲目，省去保存时整库刷新；通知回到 UI 线程，退出等待在途保存且抑制迟到通知。
- `_tools/WebDavRegression`、`_tools/WebDavSaveUiRegression`：增加重定向版本/长度/Range 回归及真实 WinUI 凭据保存、UI 心跳、失败和退出收尾验证；真实 OpenList 目录 77 首元信息全部通过，抽样验证标签、封面、音频字节及实际 AudioPlayer 解码和定位。

## 2026-09-25 播放进度条右侧改显剩余时间

- `Services/PlaybackProgressService.cs`：滑块右侧文本由总时长改为剩余时间（总时长 − 当前进度，倒数归零），`curMs` 越过 `totalMs` 时钳到 0；SMTC 时间线仍上报总时长。
- `State/PlaybackState.cs`、`ViewModel/AppViewModel.cs`：`TotalTimeText` 更名 `RemainingTimeText`，初始值不变。
- `View/MainPage.xaml`、`View/PlayingDetailPage.xaml`：`PlayTimeTextBlock`/`PlayTimeTextBlockPlayingDetail` 绑定改指 `RemainingTimeText`。

## 2026-09-25 修复来源探活误停播放及待播期间暂停失效

- `ViewModel/Pages/WebDavSourcesViewModel.cs`：来源状态通知只更新来源展示，播放服务继续负责真实断流的停止与自动恢复，完整缓存播放不再被探活失败打断。
- `Services/PlaybackCommands.cs`：播放栏切换按钮实际请求暂停时取消待播选择，避免迟到的 WebDAV 探活再次启动歌曲。
- `_tools/PlaybackNavigationRegression`：覆盖来源通知、断流后自动切歌及探活期间暂停的交互回归。

## 2026-09-25 修正播放进度条滑块端点裁切

- `Style/SilderDictionary.xaml`：移除滑块负边距，使其留在 Slider 行程内；将轨道与已播放段对齐到滑块中心，修正两端显示。

## 2026-09-25 修正 WebDAV 续播时进度条短暂归零

- `Services/PlaybackCoordinator.cs`、`Services/BassPlayerCommandService.cs`：断流后的队列恢复耗尽时保留当前进度和失败状态，停止播放器时不清空进度条。
- `Services/RemotePlaybackService.cs`、`ViewModel/Pages/WebDavSourcesViewModel.cs`：来源离线导致会话停止后继续向进度轮询提供最后位置，续播会话接管后再切换快照；明确停止仍清除保留状态。
- `_tools/PlaybackNavigationRegression`、`_tools/RemotePlaybackRegression`：验证恢复耗尽、来源停止与显式停止时的进度行为。

## 2026-09-25 修复 WebDAV 断流后播放从头开始

- `Services/RemotePlaybackService.cs`、`Services/PlaybackCoordinator.cs`、播放命令入口：保留断流前最后一次解码进度，连接恢复后点击播放恢复同一首的当前位置；主动重新选曲仍从头开始。
- `External/BassPlayerIpc.Shared/Streaming.cs`、`External/AudioPlayer/Playback/PlaybackEngine.Streaming.cs`、`Player/AudioPlayer.exe`：准备远程流时携带起播位置，在解码器就绪前完成定位，并同步更新发布用播放器。
- `_tools/RemotePlaybackRegression`、`_tools/StreamingRegression`：覆盖断网、重连、恢复进度和主动选曲语义。

## 2026-09-25 修正播放进度条两侧时间文字对齐

- `View/MainPage.xaml`、`View/PlayingDetailPage.xaml`：移除时间文字的 6 px 下边距，使其与紧凑进度条的轨道垂直居中。

## 2026-09-25 收紧播放进度条垂直占位

- `Style/SilderDictionary.xaml`、`View/MainPage.xaml`：播放进度条模板高度从 44 px 收至 24 px，并收紧主播放栏最小高度，缩小两处进度条下方的留白。

## 2026-09-25 修正播放进度条外观并撤回不准确的缓冲显示

- `Style/SilderDictionary.xaml`、`View/MainPage.xaml`、`View/PlayingDetailPage.xaml`：恢复 HyPlayer 的 12 px 圆形滑块和中性色轨道，对齐两侧时间文字。
- `Services/RemotePlaybackService.cs`、`State/PlaybackState.cs`、`ViewModel/AppViewModel.cs`：撤回以解码器待播帧绘制的缓冲亮段；断网后解码仍可能消耗已下载字节，现有链路缺少可靠的下载字节到播放时间映射，避免误示网络缓冲。

## 2026-09-25 播放进度条移植 HyPlayer 布局并显示网络缓冲

- `View/MainPage.xaml`、`View/PlayingDetailPage.xaml`、`Style/SilderDictionary.xaml`：进度条采用已播时间、强调色滑块、总时长的三段布局；轨道叠加低亮度的网络缓冲进度。
- `State/PlaybackState.cs`、`ViewModel/AppViewModel.cs`、`Services/PlaybackProgressService.cs`、`Services/RemotePlaybackService.cs`：从远程状态的当前位置与已解码时长计算缓冲终点，按会话代次发布到 UI；选曲、失败和停止时清除旧缓冲。

## 2026-09-25 新增土耳其语界面

- `Strings/tr/Resources.resw`：新增土耳其语资源 783 条，键集合与 en 完全一致，占位符逐条校验无差异。
- `App.xaml.cs`：系统语言检测链新增 `tr` 分支，系统语言为土耳其语时自动切换应用语言。

## 2026-09-25 协议同意记录迁至文档目录

- `Services/AgreementAcceptanceStore.cs`：`agreement.json` 从本地应用数据目录（MSIX 下被虚拟化重定向，部分用户环境无法读写）迁至 `Documents\OriginalSoundPlayer\Agreement\`，与数据库/设置同根；不回退读取旧位置，升级后存量用户需重新确认一次协议。
- `Legal/legal.zh-CN.json`、`Legal/legal.en.json`：隐私文档中的存储位置声明同步更新。

## 2026-09-25 WebDAV 扫描无可见变更不再整库重建；本地来源行图标对齐

- `Services/MusicDatabaseService.WebDav.cs`：目录批次提交返回可见新增数（新插入 + 缺失复现）；目录/来源收尾标记返回本次转入缺失的行数（`Missing` 条件收紧为仅 0→1）。
- `Services/WebDavLibraryService.cs`：目录阶段结束的整库重载改为仅在存在可见增删时执行，服务端无变化时不再重建曲目列表（消除启动自动扫描结束时的一次闪烁）。
- `View/AddFolderPage.xaml`：本地文件夹行的重扫/移除按钮改为 34×34、图标字号 16、面板 Spacing 2，与 WebDAV 来源行一致。

## 2026-09-25 消除 WebDAV 启动扫描列表闪烁并常驻来源曲目数

- `Services/WebDavLibraryService.cs`：扫描批次发布不再逐批 `NotifySongsSourceChanged`（原先每个目录/每 64 首/每 16 条元数据整表 Reset 曲目列表，启动自动扫描期间持续闪烁），新增曲目沿用本地扫描的增量追加口径；元数据阶段实际写入后收敛一次排序与各页投影。
- `Services/MusicDatabaseService.WebDav.cs`：新增按来源统计可见远程曲目数（`Missing = 0`）的查询。
- `ViewModel/Pages/WebDavSourcesViewModel.cs`、`View/SubView/WebDavSourcesControl.xaml`：WebDAV 来源行常驻显示曲目数（与本地文件夹行同款式、复用 `FolderNumberOfSongs` 文案），加载时填充、扫描结束（完成/失败/取消）后刷新。

## 2026-09-24 分离 WebDAV 选中状态、缓存可播放性与断流恢复

- `PlaybackState.cs`、`PlaybackCoordinator.cs`、列表 ViewModel：待播行立即选中，实际播放信息延后提交；连续切歌取消旧等待，停止取消待播，旧结果不会覆盖新选择。
- `WebDavLibraryService.Availability.cs`、`WebDavAvailability.cs`：完整缓存优先于来源探活；来源失败后按单调时钟暂缓自动重试 15 秒，直接点选可立即重试，迟到探活不能抹掉新的断流状态。
- `RemoteReadSession.cs`、`RemotePlaybackService.cs`：缓存读取不取凭据、不联网；源端读失败发布来源状态并触发有界队列恢复，文件/解码错误不误报整台服务离线；当前会话与待播探测独立取消。
- `Music.cs`、`BindUtils.cs`、列表行和播放页：缓存歌曲在来源离线时保留 CloudDownload 图标、正常透明度和播放控制；停止按钮在离线状态仍可用。
- `_tools/PlaybackNavigationUiRegression`、`RemotePlaybackRegression`、`WebDavRegression`：验证真实 WinUI ListView 即时选中、实际读取链路断流恢复、离线缓存、缓存清除、重试期限和会话取消边界；播放协议端为可控测试端，未替代真实 NAS 与音频设备验证。

## 2026-09-24 修复 WebDAV 切歌探活并标记完整音频缓存

- `PlaybackCoordinator.cs`、`PlaybackCommands.cs`、`BassPlayerCommandService.cs`：上下首重新确认 WebDAV 实际在线状态；失败继续按队列方向寻找下一首，同次请求不重复探测失败来源，全部不可用时有界结束。
- `MusicCommands.cs`、`WebDavLibraryService.cs`：探活期间保留切歌入口，再按上下首从正在探测的歌曲继续，新选择立即取消旧选择的等待；共享探活不会被旧调用方取消而误报在线。
- `PlaybackCoordinator.cs`、`RemotePlaybackService.cs`：远程凭据、准备及缓存会话收尾在后台执行，避免同步磁盘操作拖住 UI；停止代次同步登记，迟到停止不覆盖新选曲。
- `RemoteAudioCache.cs`、`WebDavLibraryService.cs`、`Music.cs`、列表行及播放栏：完整落盘的当前版本音频显示 CloudDownload（EBD3），缓存清理、目录更换和版本变化同步更新；新增六语言提示词。
- `_tools/PlaybackNavigationRegression`、`PlaybackNavigationUiRegression`、`WebDavRegression`：增加失联/恢复、连续失败、取消、UI 调度和完整缓存状态回归。

## 2026-09-24 放开离线曲目手动播放并弱化列表行

- `Services/MusicCommands.cs`、`Services/PlaybackCommands.cs`、`View/MainPage.xaml`、`View/PlayingDetailPage.xaml`：手动播放按钮允许离线 WebDAV 曲目进入播放前探活流程，探活失败后仍停止播放；其它传输控制继续遵循在线状态守卫。
- `View/Controls/MusicListRowControl.xaml`、`Utils/BindUtils.cs`：离线歌曲行降低透明度但保留播放点击入口。
- `Utils/ToolUtils.cs`：右键“播放”保留为可用，转换、歌词、打开位置和 USB 等需要读取内容的操作继续置灰。

## 2026-09-24 抽取音乐列表/网格模板并同步 WebDAV 探活信息

- `View/Controls/MusicListRowControl.xaml`、列表页：将普通列表行、分组详情行和播放列表行抽成可配置行控件，保留外层 `ListView` 虚拟化与选择/右键行为。
- `View/Controls/*GridCardControl.xaml`、专辑/艺术家/文件夹页：按页面分别抽取 GridView 卡片模板，保留外层 `GridView` 的虚拟化和语义缩放。
- `ViewModel/Pages/WebDavSourcesViewModel.cs`：来源管理页在加载和探活状态变化时刷新 WebDAV 信息，离线状态不再只更新播放守卫而遗漏来源卡片。

## 2026-09-24 独立探测 WebDAV 在线状态并同步右键菜单

- `Services/WebDav/WebDavTransport.cs`、`WebDavLibraryService.cs`：使用 `PROPFIND Depth:0` 轻量探活，启动检查所有启用来源并按前台播放状态自适应轮询；在线状态不再依赖目录扫描结果。
- `Model/MenuModel.cs`、`Extensions/MenuFlyoutExtensions.cs`、`Utils/ToolUtils.cs`、各列表/网格 ViewModel：WebDAV 离线时将播放、转换、歌词、资源打开和 USB 发送等需要读取内容的右键操作置灰。
- `Services/PlaybackCoordinator.cs`、`ViewModel/Pages/WebDavBrowserViewModel.cs`：播放和 WebDAV 浏览前执行在线检查，避免探活状态过期后绕过离线守卫。

## 2026-09-24 标记离线 WebDAV 曲目并禁止播放

- `Services/WebDavLibraryService.cs`、`Model/Music.cs`：启动扫描失败时发布来源离线状态，同步到曲目运行时状态；恢复连接后清除。
- `View/*`、`Utils/BindUtils.cs`：离线来源标识改用 `F384`，列表播放入口和当前播放控制同步置灰。
- `Services/PlaybackCoordinator.cs`、`PlaybackCommands.cs`、`BassPlayerCommandService.cs`：在播放协调器、手动切歌和自动切歌路径统一跳过离线曲目。

## 2026-09-24 合并本地与 WebDAV 的同名专辑

- `Services/LibraryProjectionService.cs`、`ViewModel/Pages/AlbumViewModel.cs`、`MusicGroupDetailViewModel.cs`：本地与 WebDAV 同名专辑只显示一张卡片，进入详情后跨来源显示所有歌曲；不同来源的同名曲目也保留。
- `Services/LibraryQueries.cs`、`_tools/SharedStateRegression`：专辑歌曲数按来源分别计数，并增加跨来源专辑投影、详情和同名曲目回归检查。

## 2026-09-24 修复 WebDAV 网络源响度偏低

- `External/AudioPlayer/Playback/Session.cs`、`PcmEffects.cs`、`LoudnessScanner.cs`：WebDAV HTTP 会话纳入与本地文件相同的 EBU R128 后台响度分析；分析完成前使用中性增益，避免原先固定 −12 dB 保守衰减导致网络歌曲整体偏低。
- `External/BassPlayerIpc.Shared/Streaming.cs`、`Services/RemotePlaybackService.cs`：传递远程文件长度和 ETag 作为响度缓存版本，文件更新后自动重新分析；后台分析不阻塞首次播放，慢速网络仍可先播放。

## 2026-09-23 修复切歌时封面与 Win2D 资源持续累积

- `Controls/ImageSwitcher`、`PlayingDetailPage.xaml`：隐藏详情页时取消封面读取，原图解码宽度限制为 1536，过渡完成后释放上一张图源，并保留快速恢复所需的当前图像。
- `CoverLoadQueue`、`AlbumCoverBehavior`、`FadeImageBehavior`：缩略图任务共享像素数据而非共享 `SoftwareBitmapSource`，各控件独立释放 WinRT 图像源；取消或替换时及时回收资源。
- `CoverPresentationService`、`SystemMediaControlsService`、`ToolUtils`：切歌时取消过期封面/媒体控制任务及原图读取，避免大封面被旧任务链延迟持有。
- `LyricsRenderCoordinator`、`AlbumArtControl`、`NowPlayingCanvas`：连续换词时立即释放被覆盖的 Win2D 待销毁行，修复丢弃池化帧和卸载时的 Win2D 视觉树引用，关停时完整清理文本布局、几何和缓存效果。

## 2026-09-23 修复 WebDAV DSF 采样率显示为八分之一

- `Services/WebDav/RemoteMetadataProbe.cs`：将 FFmpeg 对 DSF/DFF 暴露的 DSD 字节率换算为实际 DSD 采样率，并按一位样本写入元数据。
- `Services/MusicDatabaseService.WebDav.cs`：后续元数据扫描自动重读旧版本已保存的八分之一采样率。
- `_tools/WebDavRegression`：增加 DSF 64/128/256 回归检查，覆盖采样率、位深、时长和有界读取。

## 2026-09-23 精简 WebDAV 来源卡片操作

- `View/SubView/WebDavSourcesControl.xaml/.cs`：移除无用的“歌曲”跳转按钮；来源图标由纯装饰 Border 改为与 AddFolderPage `OpenFolderButton` 同款的可点击按钮，直接打开 WebDAV 目录浏览，操作行不再保留重复的浏览按钮；清理不再使用的 using。
- `Strings/*/Resources.resw`：删除全部语言中不再使用的 `WebDavSongsAction` 资源。

## 2026-09-23 修复 WebDAV 目录选择、曲库范围和来源回退

- `WebDavConnectionDialog.xaml/.cs`、`WebDavConnectionViewModel.cs`、`WebDavTreeItem.cs`：模型管理父子级联与部分选中，懒加载子目录继承选择；扫描根独立保存，勾选所有子目录不会意外扩大到父目录，取消父目录清空子树，取消单个子目录保留其余范围。
- `MusicDatabaseService.WebDav.cs`、`MusicDatabaseService.cs`、`WebDavLibraryService.cs`：保存来源时立即排除范围外的旧索引，曲库读取过滤缺失曲目并刷新当前视图；重新扫描纳入的歌曲复用原 ID，保留收藏和歌单映射。
- `WebDavSourcesViewModel.cs`：当前来源被移除且 ComboBox 清空选项后，选择和过滤器统一回退到“全部来源”；移除其他来源保留当前选择。
- `_tools/WebDavTreeUiRegression`：链接生产对话框、ViewModel、SQLite 方法，覆盖真实复选框点击、级联与保存、范围缩小及重载、曲目身份保留和真实 ComboBox 删除回退。已被扩大并保存的旧配置仍需重新选择原目录。

## 2026-09-23 修复升级后播放列表卡片封面为空

- `Model/PlayList.cs`、`View/PlayListPage.xaml`：封面绑定改为可通知的 `CoverMusic`，修复界面先于歌曲映射加载时，按不变 ID 取图后不再刷新的问题；该属性不写入数据库，无需重建歌单。
- `Services/PlaylistSummaryProjection.cs`、`ViewModel/AppViewModel.cs`、`Services/MusicDatabaseService.cs`：在 UI 线程随音乐库、歌单成员和顺序更新封面及数量，单次遍历成员选择默认排序的首曲；删除后读取最新成员，移除详情页重复查询。
- `_tools/PlaylistCoverRegression`：链接真实页面 XAML、图片行为和投影，覆盖延迟加载、同数量排序、移除曲目、新建/空歌单及通知；1.2.5.0 原始 XAML 复现失败，修复后通过。主程序 x64 Release 构建 0 错误。

## 2026-09-23 修复本地 FLAC 曲尾错误导致播放停住

- `External/AudioPlayer/Decode/PcmDecoder.cs`：有限次恢复无效编码帧并继续读取到真实 EOF，修复曲尾错误导致不发结束通知、自动切歌及 seek 失效；真实 I/O 失败仍保留失败语义。
- `Player/AudioPlayer.exe`：更新 NativeAOT 播放器产物。
- `_tools/PlaybackSwitchRegression`、`_tools/AudioPlayerSmokeTest`：新增自行生成的曲尾损坏 FLAC、完整解码及 EOF 后 seek、DirectSound 淡入淡出和原生结束通知验证；用户文件完整播放 292.667 秒通过，播放回归 295/295、网络回归 50 项通过。
- `docs/PlaybackEndInvestigation.md`：记录复现证据及 1.2.2.0 以来播放改动的目的，定位回归来源为 `636f4844` 的共用解码错误处理。

## 2026-09-23 修复 WebDAV 树节点显示类型名

- `WebDavBrowserDialog`、`WebDavConnectionDialog`：模板按实际的 `TreeViewNode` 类型读取 `Content` 中的数据并创建名称和图标，修复控件类型名直出和折叠后复用视图引发的异常。
- `_tools/WebDavTreeUiRegression`：链接生产 XAML 和节点模型，运行真实 WinUI 对话框验证显示、多选和展开行为。

## 2026-09-23 WebDAV 目录对话框改为多选树

- `WebDavConnectionDialog`、`WebDavConnectionViewModel`、`WebDavTreeItem`：按展开加载多层目录，恢复已保存的深层音乐根目录，并对父子重复选择去重。
- `WebDavBrowserDialog`、`WebDavBrowserViewModel`：按展开浏览远端目录，勾选多首已入库歌曲后按选择顺序建立播放队列；六种语言资源同步更新。
- `WebDavConnectionDialog`、`WebDavBrowserDialog`：通过节点映射关联目录数据，将 `SelectedNodes` 的勾选变化同步到 ViewModel。
- `_tools/WebDavRegression`：增加目录树深层选择、保存与恢复回归。
- `docs/WebDAV接入设计方案.md`：同步目录树与多选播放交互说明。

## 2026-09-23 修复短曲目偶发结束后不自动切歌

- `Services/BassPlayerCommandService.cs`、`Services/AutoAdvanceGate.cs`：自动切歌执行中保留一次新的结束请求，待当前选曲与界面状态完成后继续处理；执行异常记录日志，不让单飞状态卡住。
- `_tools/AutoAdvanceRegression`：覆盖在途切歌期间再次结束、重复结束合并和退出清理。

## 2026-09-22 卷积曲线预设改存用户文档目录

- `Services/CurvePresetService.cs`：`ConvolutionCurves.json` 由 MSIX LocalState 改存 `Documents\OriginalSoundPlayer\Settings\`（与 Settings.json、AudioCorrections.json 同目录），卸载重装/重新部署不再丢失预设；Documents 不可用时回退 LocalState。不做旧位置迁移。
- `External/AudioPlayer/DSP.md`：同步预设存储位置说明。

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
