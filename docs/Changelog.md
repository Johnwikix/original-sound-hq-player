# 功能变更记录

新条目加在最上方。

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
