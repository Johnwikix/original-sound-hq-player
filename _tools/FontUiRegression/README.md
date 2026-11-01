# 程序字体 WinUI 回归

在仓库根目录运行 `./_tools/FontUiRegression/Run.ps1`。使用实际 WinUI、Toolkit、生产偏好状态与字体资源服务；构建前从生产常规/歌词设置提取三张字体卡片，保留编译绑定与控件布局，仅替换 ViewModel 路径。

验证默认 Segoe UI、字体即时刷新、浅/深/跟随系统主题、图标及显式字体保留、普通/桌面独立歌词字体开关/置灰/选择恢复、生产桌面歌词 ViewModel 两种模式的即时样式通知、源生成 JSON 兼容与关闭后的迟到更新。与字体无关的歌词文件优先级及语言显示使用最小适配器；不代替完整播放器的人工高对比度/字形验收。

桌面字体回归使用生产 DesktopLyricsViewModel、偏好状态与样式记录；数据库写入和原生窗口创建采用桩，不代替实际任务栏嵌入显示验证。

缓存 ContentDialog 回归链接生产 DialogHelper，先确认修复前同实例关闭/换字体/重开在 Light 下标题保留 Consolas、资源已是 Arial；修复后在 Light/Dark/Default 下重复验证标题与局部样式内容、按钮、模板/输入内容保留及图标/显式字体不受覆盖。

启动主题回归链接生产 ThemeStyleHelper、ApplicationFontService 及 Mica/亚克力背景，使用真实 WinUIEx `WindowContent` 包装层及其 RequestedTheme 同步回调。旧测试直接设置 `Window.Content`，绕过了这个关键边界。修复前已确认首次 Loaded 字体注册把外层 Light 改为 Default；修复后验证浅/深/跟随系统在启动、实时换字体、设置主题后再换字体和重新挂接时均保留，内层不新增局部主题，并检查新页面前景和背景主题。启动后不追加 SetAppTheme；完整播放器重启仍需单独验收。
