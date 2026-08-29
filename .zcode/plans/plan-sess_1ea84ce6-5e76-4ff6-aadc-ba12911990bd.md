## 根因

保存/还原两端都是物理像素、坐标系一致,机制本身没错。问题在上游 WinUI:

- **microsoft-ui-xaml#10498**(Open,未修复):`AppWindow.MoveAndResize` 是"先 resize 后 move",resize 发生在窗口当前所在显示器(启动时=主屏)的 DPI 上下文中,跨 DPI 还原时尺寸被错误缩放。
- **microsoft-ui-xaml#6720**(WinAppSDK 1.1.2 起的行为):窗口跨 DPI 显示器移动时,WinUI 自动处理 `WM_DPICHANGED` 并按建议矩形缩放窗口尺寸,还原时再次放大/缩小尺寸。

主屏还原正常是因为无 DPI 切换;子屏 DPI≠主屏时,MoveAndResize 的旧 DPI 解释 + 移动后的自动缩放叠加,导致尺寸错误。

## 修复(只动还原端,保存端与 JSON 格式不变)

### 1. `Helper/WindowSizeHelper.cs` 新增统一辅助方法

```csharp
/// <summary>
/// 跨显示器安全的移动+还原尺寸。替代 AppWindow.MoveAndResize:
/// MoveAndResize 内部先 resize 后 move(microsoft-ui-xaml#10498),resize 会按
/// 移动前所在显示器的 DPI 解释;且移动到不同 DPI 显示器时 WinUI 会自动按
/// WM_DPICHANGED 缩放尺寸(WinAppSDK 1.1.2 起)。先 Move 让 DPI 切换完成,
/// 再 Resize 施加精确物理尺寸,可保证还原值与保存值逐像素一致。
/// </summary>
public static void MoveAndResizeExact(AppWindow appWindow, int x, int y, int width, int height)
{
    appWindow.Move(new PointInt32(x, y));
    appWindow.Resize(new SizeInt32(width, height));
    // 兜底:若 DPI 切换的自动缩放异步落在 Resize 之后,重放一次精确尺寸
    if (appWindow.Size.Width != width || appWindow.Size.Height != height)
        appWindow.Resize(new SizeInt32(width, height));
}
```

### 2. 替换 3 个还原调用点为 `MoveAndResizeExact`

- `MainWindow.xaml.cs:150`(SetWindow,主窗口还原,同时覆盖 IsMaximized 先还原矩形再最大化的路径)
- `DesktopLyricsWindow.xaml.cs:152`(ConfigureWindow,桌面歌词还原)
- `DesktopLyricsWindow.xaml.cs:115`(ApplyDefaultBounds,重置默认位置,保持一致性)

### 3. 顺带修复同类潜伏问题:`WindowSizeHelper.ResizeWindowAndCenterInMainWindow`(WindowSizeHelper.cs:102-114)

详情窗口(AlbumDetailWindow/MusicDetailsWindow)目前用**新窗口创建位置所在显示器**的 `GetDpiForWindow` 计算物理尺寸,主窗口在子屏时逻辑尺寸也会错。改为:先 `Move` 到主窗口居中位置 → 再读 `GetDpiForWindow(hwnd)`(此时已反映目标显示器 DPI)→ 按该 DPI 把逻辑尺寸换算成物理尺寸 → `Resize`。

## 不改的部分(确认无需改动)

- 保存端(`App.xaml.cs SavePlayStateAsync`、`DesktopLyricsManager.PersistBounds`、`AppWindow.Changed` 内存跟踪):继续存物理像素,round-trip 正确。
- `IsBoundsOnScreen` 离屏校验:物理像素比较,无需动。
- JSON schema(`SavePlayState`/`SaveDesktopLyricsState`):无迁移。
- 桌面歌词拖动(GetCursorPos + `AppWindow.Move`):position-only 移动无 DPI 问题,不动。

## 验证(需要你在多屏不同 DPI 环境实测)

1. 主屏:移动/缩放主窗口 → 退出重启 → 位置尺寸逐像素一致(现状回归)。
2. 子屏(DPI≠主屏):把主窗口拖到子屏缩放 → 退出 → 核对 `PlayState.json` 中 W/H 与重启后 `AppWindow.Size` 一致;桌面歌词同理核对 `DesktopLyricsState.json`。
3. 子屏上最大化退出 → 重启 → 恢复最大化;取消最大化后落回正确还原矩形。
4. 主窗口在子屏时打开专辑/歌曲详情窗口 → 尺寸为预期逻辑尺寸(700×550 / 850×700)× 子屏 DPI。

若实测仍偶发尺寸漂移(说明 WinUI 对 WM_DPICHANGED 的处理是异步的,兜底重放不够),备选方案是还原端改 P/Invoke `SetWindowPos`(SWP_NOZORDER|SWP_NOACTIVATE)单次原子设置,这是 #10498 评论区确认的另一条 workaround——先不引入,保持纯公开 API。