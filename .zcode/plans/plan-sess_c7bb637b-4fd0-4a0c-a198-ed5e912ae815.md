### 目标
桌面歌词自适应取色的采样区域从"环绕整个窗口矩形"改为"环绕当前行实际绘制文本边界"：渲染器上报每行文本区域 → 窗口换算屏幕坐标 → 采样其外围 36px 环带。窗口外圈环带降级为无文本时的回退。不改 External/AnimatedWin2dControls 共享库。

### 改动清单

**1. 实现前置检查**
- grep `RenderLyricsLine` 的 `PrimaryPosition`/`SecondaryPosition`/`PrimaryTextLayout`/`SecondaryTextLayout` 可访问性（CanvasLyricsRenderer 已在用 `PrimaryTextLayout`，应为 public）。若 Position 属性非 public，改用其他公开成员合成边界并在实施时调整（不影响其余步骤）。

**2. DesktopLyrics/IDesktopLyricsRenderer.cs**
- 接口新增 `Rect? LastTextBounds { get; }`（元素坐标 DIP，主文本+翻译行合并边界；null = 当前无文本）。

**3. DesktopLyrics/CanvasLyricsRenderer.cs**
- 新增不可变盒子类 + `volatile` 引用字段（RebuildLine 在渲染线程写、UI 线程读，引用赋值原子防撕裂）。
- `RebuildLine` 在 `MeasureAndArrange` 成功后计算边界：主文本 `PrimaryTextLayout.LayoutBounds` 偏移 `PrimaryPosition`，翻译行同理，`Rect.Union` 合并；**再加上垂直居中偏移 `offsetY = max(0, (height - line.BottomRightPosition.Y) / 2)`**（OnCanvasDraw 绘制时才平移，上报必须补上；width/height 参数 RebuildLine 已有）。
- `DisposeCurrentLine` 置空盒子；`LastTextBounds` 属性读盒子。

**4. DesktopLyrics/TextBlockLyricsRenderer.cs**
- `LastTextBounds` 惰性计算（纯 UI 线程，无需事件钩子）：`_mainPair.Main.TransformToVisual(null).TransformBounds(...)`，翻译行可见则 `Rect.Union`；文本为空或宽高 ≤1 返回 null。

**5. DesktopLyrics/DesktopLyricsAdaptiveColor.cs**
- 抽出环带采样核心 `SampleRing(x, y, w, h, ...)`（现窗口版四条带逻辑原样保留，只是矩形来源参数化，虚拟屏裁剪照旧）。
- 新增 `TrySampleBackgroundLuminance(int x, int y, int width, int height, out double luminance)`（任意屏幕矩形的外圈环带）；保留 `IntPtr hwnd` 重作回退路径。

**6. DesktopLyrics/DesktopLyricsWindow.xaml.cs**
- `RefreshAdaptiveColor`：先取 `_renderer?.LastTextBounds`；有效则经 `MapToScreen`（`ClientToScreen` 客户区原点 + `RootGrid.XamlRoot.RasterizationScale`，与现有 ControlPanel 命中测试同款换算）得屏幕矩形 → 调矩形版采样；无文本/换算失败回退现有 `hwnd` 版。滞回判定逻辑不变。
- 新增私有 `MapToScreen(Rect)`；更新相关注释。

### 保留不动
External 库全部文件、滞回阈值（128±16）、1s 轮询节奏、自定义颜色覆盖、扫光 40% 预混、翻译行 0.6 透明度。

### 验证
`dotnet build -c Debug -p:Platform=x64` 零错误；人工验证路径：短行/长行/无歌词(间奏)三种状态下采样区域分别贴合文本行、无异常回退日志、黑白切换仍稳定。