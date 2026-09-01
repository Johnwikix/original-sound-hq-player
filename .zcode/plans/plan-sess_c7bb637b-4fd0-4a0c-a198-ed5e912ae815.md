### 目标
完全移除桌面歌词的描边体系（渲染、样式、设置、UI、资源），保留自适应取色、自定义颜色覆盖、扫光修复与翻译行 0.6 透明度。不改 External/AnimatedWin2dControls 共享库（主窗口渲染零影响），桌面侧向库传描边宽度 0。

### 改动清单

**1. DesktopLyrics/DesktopLyricsStyle.cs**
- 删除 `Outline`、`OutlineWidth` 两个参数，更新文档注释。

**2. DesktopLyrics/DesktopLyricsViewModel.cs**
- `BuildStyleFromSettings()` 删除对应的两个实参。

**3. DesktopLyrics/TextBlockLyricsRenderer.cs（最大简化）**
- 删除 `OutlineOffsets`、`_shadowStacks`、`_shadowPairs`、`_outline`、`_outlineWidth`。
- 构造函数不再创建 4 层阴影栈，只保留唯一主栈；`BuildStack(bool isShadow)` 去掉阴影分支（无 RenderTransform，Foreground = 主色刷）。
- `_switchAnimator` 改绑唯一栈的 main+trans 两个 TextBlock；`ShadowAndMainPairs` 属性删除，`UpdateText`/`ApplyFont` 改为直接遍历 `_mainPair`。
- 删除 `ApplyOutlineWidth()` 及 SetStyle 中的 Outline 开关分支与 `_outlineWidth` 赋值；更新类注释。

**4. DesktopLyrics/CanvasLyricsRenderer.cs**
- 删除 `_outline`、`_outlineWidth` 字段及 SetStyle 对应行。
- `OnCanvasDraw`：`EnsureCaches(sender, 0)`；删除 `strokeWidth` 计算、`IsStrokeEnabled` 赋值、`UnplayedStrokeTint.Color = Colors.Black` 赋值、LRC 整行描边垫底 `DrawImage` 块及相关注释。
- `RebuildLine`：`MeasureAndArrange(...)` 描边宽度实参传 0。
- 更新类头注释与 `UnplayedOpacity` 注释（不再引用"黑描边内半环"，保留不透明预混方案）。
- 库侧 `RenderLyricsLine.EnsureCaches`/`LyricsLayoutManager.MeasureAndArrange`/`LyricsLineRenderer.IsStrokeEnabled` 均不改，仅以 0/默认值调用。

**5. 设置链删除 `IsDesktopLyricsOutlineEnabled` 与 `DesktopLyricsOutlineWidth`**
- Model/AppSettings.cs（L52、L64 附近两行）
- Model/SaveSettings.cs（两个属性）
- ViewModel/AppViewModel.Settings.cs（`IsDesktopLyricsOutlineEnabled` ~L599、`DesktopLyricsOutlineWidth` ~L788 两个属性）
- Services/MusicDatabaseService.cs：GetSettingsAsync 读取两行（~L976/L979）与 AppViewModel 回填两处、SaveCurrentSettings 写入两行（~L1199/L1209）。
- 兼容性：旧 Settings.json 中残留的这两个 key 反序列化时被忽略、下次保存自然消失，无需迁移。

**6. View/SettingsPage.xaml**
- 删除"桌面歌词描边"ToggleSwitch 行（~L1573-1587）与"描边宽度"Slider 行（~L1811-1841）。

**7. Strings/{zh-CN,en,de,es,ja,ru}/Resources.resw**
- 删除 `DesktopLyricsOutline` 与 `DesktopLyricsOutlineWidth` 条目。

### 保留不动
自适应取色采样器与滞回逻辑、`UseCustomColor`/`IsDesktopLyricsCustomColorEnabled` 覆盖开关及其 UI、`ComputeUnplayedFill()`（0.4 提亮/压暗）、翻译行 0.6 透明度、发光等逐字动效、External 库全部文件。

### 验证
`dotnet build -c Debug -p:Platform=x64` 零错误；grep 确认 `Outline`/`OutlineWidth`/`IsStrokeEnabled` 在 DesktopLyrics 目录与设置链无残留引用。