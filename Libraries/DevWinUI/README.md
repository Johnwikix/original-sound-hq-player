# 本地维护的 DevWinUI 移植

上游：DevWinUI 10.8.0，提交 `28d9f74fc1ad86d6597b6ad43b6122e52a1b49aa`。
源码与 MIT 许可见 [LICENSE-Controls.txt](LICENSE-Controls.txt)，随构建和发布输出至 `Licenses/DevWinUI-Controls.txt`。

## 完整移植范围

- `Controls/AutoScrollView.cs`：`Child`、`IsPlaying`（默认 true）、`Spacing`（默认 20）、`ScrollingPixelsPreSecond`（默认 30，保留上游拼写）及双副本合成动画。
- `Controls/RedirectVisualView.cs`：完整基类和扩展点，包括内容重定向、测量模式、模板部件及源画刷偏移。
- `Controls/CompositionShadow*.cs`：保留 `Content`、`Mask`、`DropShadow`、`Visual`、`compositor`、`GetDropShadow`、全部阴影属性、标量 `CornerRadius`、`UseCornerRadius`、`IsRounded`、`IsBreathing`、`StartBreath`/`StopBreath`、`UseEdgeColorFromImage` 和 `ImageSourceForEdgeColor`。
- `Themes/Generic.xaml`：完整模板及命名默认样式，应用合并加载；滚动控件沿用基类的默认样式键。
- `CompositionShadowImageHelper`：移植必要的 URI 解析、图像加载和四边平均取色。保留角点重复计入及不按 alpha 加权的上游规则，使用项目已有的 Win2D。
- 已有轮播布局和动画移植继续使用 [LICENSE-StoreCarousel.txt](LICENSE-StoreCarousel.txt)。

## 本地维护调整

- 命名空间改为 `WinUIMusicPlayer.Controls`；页面沿用原事件、绑定与布局，移除 DevWinUI 和 DevWinUI.Base 包依赖。
- 修正滚动速度依赖属性错误注册到 `OpacityMaskView` 的上游问题，排除非有限速度/间距和零宽内容的无效动画。
- 滚动尺寸事件合并排队并复用委托；内容和视口改变时更新动画时长。停止、卸载或换模板时停止动画、解除源视觉引用、恢复原裁剪，重新加载后恢复。
- 阴影保留圆形优先于自定义 mask、自定义 mask 优先于普通圆角的规则；外部 mask 和 clip 属于调用方，控件不释放。
- 圆形/圆角复用合成几何；退出几何模式、换内容或卸载时释放自有画刷、曲面、形状和 clip，并恢复调用方裁剪。`compositor` 字段设为只读以固定资源归属。
- 呼吸动画保留 5→30→5、默认 2 秒、交替方向和上游 sine easing 控制点；卸载停止、重载恢复，显式停止恢复 `BlurRadius` 设置。普通阴影属性回调按上游仅更新对应 native 值，颜色、偏移、透明度和异步取色不再中断呼吸或覆盖其他 native 配置。
- 取色只读取四边，省去整图颜色数组及临时 List；请求支持取消和版本检查。异常被观察并回退黑色，卸载后不发布迟到结果，属性更新保留 UI 上下文。

## 回归

运行 `_tools/LocalControlsUiRegression/Run.ps1`，使用真实 WinUI 窗口、Composition、Win2D 和本地延迟 HTTP 响应检查功能及生命周期，输出 `result.txt` 和渲染截图。主项目构建检查全部页面引用；`HomeUiRegression` 检查实际列表行集成。

2026-11-02 验证结果：主项目 x64 Debug 构建通过；独立控件真实 UI 回归 38 项通过；`HomeUiRegression.exe --local-controls` 的实际列表行/页面集成 19 项通过。源代码公开成员对照未遗漏，主项目及相关回归工程的还原图均无 DevWinUI 包，许可证已进入主程序输出。未进行 Windows 10 实机和完整应用逐页人工验收。

后续呼吸动画修复验证：新增真实属性组合回归后共 50 项，连续 3 次全部通过；主项目 x64 Debug 构建通过。嵌套卸载测试等待内外层自己的 Unloaded，生产控件的卸载逻辑未作修改。

扩展回归的既有失败已用独立 HEAD 基线复验：`HomeUiRegression` 全量运行均在 90 项通过后失败于 `paging dismisses the overlay before its source card moves`；`ManualLocationUiRegression` 均缺少 `ToolUtils.OpenFileInExplorer`、`AppState.Shell`、`UiLayoutDiagnostics` 适配器并引起后续 XAML 类型解析错误。这些基线问题不属于本次控件移植修复范围。
