在 `Style\ComboBoxNMenuFlyoutStyle.xaml` 中新增 `TargetType="ToggleMenuFlyoutItem"` 的隐式样式，完整复刻现有 MenuFlyoutItem 的浮动动态模板，并补上切换项特有的勾选支持。

## 根因
WinUI 的隐式样式只匹配精确类型，不作用于派生类型。`App.xaml:16` 合并的字典中仅有 `MenuFlyoutItem` / `MenuFlyoutSubItem` 的自定义样式，`ToggleMenuFlyoutItem`（`Controls\NotifyIconControl.xaml` 托盘菜单中 6 处）因此回落到系统默认模板，缺少 hover 右移 4px、按下缩放 0.97 的浮动动画。

## 修改内容（单文件：Style\ComboBoxNMenuFlyoutStyle.xaml）
在 `MenuFlyoutItem` 样式之后插入一个 `<Style TargetType="ToggleMenuFlyoutItem">`：

1. **非模板 Setter**：与 MenuFlyoutItem 样式完全一致（Background/BorderBrush/BorderThickness/Foreground/Padding/FontSize/HorizontalContentAlignment/VerticalContentAlignment/UseSystemFocusVisuals/KeyboardAcceleratorPlacementMode/CornerRadius）。

2. **模板骨架**：复刻 MenuFlyoutItem 的 `LayoutRoot`（同样的 `MenuFlyoutItemMargin`、Padding/Background/BorderBrush/BorderThickness/CornerRadius 模板绑定）+ `InnerContentRoot`（`RootScale`/`RootTranslate` 变换，RenderTransformOrigin 0.5,0.5），仅列结构差异：
   - 列定义由 2 列改为 3 列：`*`（IconRoot Viewbox + TextBlock，同现在）→ 新增 `Auto` 列放 **CheckGlyph**（`FontIcon x:Name="CheckGlyph"`，Glyph `&#xE73E;`，FontFamily SymbolThemeFontFamily，FontSize 16，Foreground `{ThemeResource ToggleMenuFlyoutItemCheckGlyphForeground}`，默认 `Visibility="Collapsed"`）→ `Auto` 列放 KeyboardAcceleratorTextBlock（Grid.Column 由 1 改为 2）。
   - CheckGlyph 放在 InnerContentRoot 内，随浮动动画一起移动。

3. **VisualStateGroups**：
   - `CommonStates`：Normal/PointerOver/Pressed/Disabled 四态的 Setter 与动画逐条照抄 MenuFlyoutItem 模板（含浮动动画），并在 PointerOver/Pressed/Disabled 三态各增加一条 `CheckGlyph.Foreground` Setter，分别指向 `{ThemeResource ToggleMenuFlyoutItemCheckGlyphForegroundPointerOver}` / `...Pressed}` / `...Disabled}`。
   - `CheckPlaceholderStates`：与 MenuFlyoutItem 模板相同的 4 个状态（NoPlaceholder/CheckPlaceholder/IconPlaceholder/CheckAndIconPlaceholder 及相同 Setter）——两者继承自同一基类、接收相同的 presenter 占位状态，保证切换项与普通项文本对齐一致。
   - `CheckStates`（新增，切换项特有）：`Unchecked` 空态、`Checked` 用 Setter 将 `CheckGlyph.Visibility` 置 `Visible`、`Indeterminate` 空态。
   - `PaddingSizeStates`、`KeyboardAcceleratorTextVisibility`：照抄 MenuFlyoutItem 模板。

## 不做的事
- 不改 `MenuFlyoutItem` / `MenuFlyoutSubItem` / `ComboBoxItem` 现有样式。
- 不处理 `RadioMenuFlyoutItem`（项目中未使用）。
- 对勾保持在右侧（与 WinUI 3 默认行为一致，即当前应用外观不变，仅补浮动动画）。

## 验证
1. `dotnet build` 编译通过（XAML 编译器会校验 TargetType 与模板结构）。
2. 运行应用 → 托盘图标右键菜单：悬停播放模式/桌面歌词切换项出现与其他菜单项一致的右移浮动动画，按下有缩放；勾选项（如当前播放模式）右侧显示对勾，未勾选不显示；Light/Dark 主题下对勾与前景色正常。
3. 由于是纯新增样式，如视觉效果不符可直接回滚该文件的单处新增。