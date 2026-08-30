## 目标

把 `DesktopLyricsWindow.xaml.cs` 中内联的 Win32 声明(`GetCursorPos`/`ClientToScreen` 两个 DllImport、私有 `POINT` 结构体、`GWL_EXSTYLE`/`WS_EX_*` 常量)提取到 `Helper/WindowHelper.cs`,与点击穿透现有的"调 WindowHelper"做法保持一致。窗口 code-behind 不再出现任何 Win32 概念。

已确认:WinUIEx 2.9.3 的 `WindowEx` 没有 `IsClickThroughEnabled`,穿透样式必须手动改 EXSTYLE,所以提取的是声明与封装,不是删除逻辑。

## 改动

### 1. `Helper/WindowHelper.cs`(新增约 25 行)

- `public struct POINT`(带 `[StructLayout(LayoutKind.Sequential)]`,`int X, Y`)——供全项目复用,消除 DesktopLyricsWindow 的私有副本
- 裸 P/Invoke(沿用本文件既有 DllImport 风格):
  - `public static extern bool GetCursorPos(out POINT lpPoint)`(带 `[return: MarshalAs(UnmanagedType.Bool)]`)
  - `public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint)`
- 私有常量 `GWL_EXSTYLE = -20`、`WS_EX_LAYERED = 0x00080000`、`WS_EX_TRANSPARENT = 0x00000020`
- 语义封装方法,复用本类已有的 `GetWindowLongPtr`/`SetWindowLongPtr`:
  ```csharp
  public static void SetClickThrough(IntPtr hWnd, bool enable)
  ```
  读 EXSTYLE → OR/CLEAR `WS_EX_LAYERED | WS_EX_TRANSPARENT` → 写回

### 2. `DesktopLyrics/DesktopLyricsWindow.xaml.cs`(净删约 25 行)

- 删除:31–33 行三个常量、369–374 行 `POINT` 结构体、376–382 行两个 DllImport
- `ApplyClickThrough`(168–178 行)改为:`_clickThrough` 幂等守卫保留 + 一行 `WindowHelper.SetClickThrough(_hwnd, enable)`
- 换调用方(逻辑不变,共 9 处):
  - `GetCursorPos(` → `WindowHelper.GetCursorPos(`(219、297、305 行)
  - `ClientToScreen(` → `WindowHelper.ClientToScreen(`(264 行)
  - `POINT` → `WindowHelper.POINT`(55 字段声明、219、239 参数、263、305 行)
- 若 `using System.Runtime.InteropServices;` 再无引用则删除

悬停轮询、按钮组命中缓存、解锁拖动等窗口行为逻辑**全部保留在窗口内**——它们是窗口行为,不是 Win32 管线代码。

## 明确不做

- `WindowSizeHelper.cs` 的私有重复声明(`POINT`、`SetWindowLongPtr`、`CallWindowProc` 等)不动:它们是该 helper 自己领域(子类化处理 WM_SIZING/WM_GETMINMAXINFO)的内聚实现,且其 `SetWindowLongPtr` 重载直接收委托,去重会引入委托生命周期复杂度,收益低。
- `App.xaml.cs` 的 `MessageBoxW` 不动(一次性启动失败弹窗)。

## 验证

1. `dotnet build WinUIMusicPlayer.csproj` 编译通过
2. 运行时手动回归点(改动为机械搬迁,行为应完全一致):桌面歌词锁定态悬停出现按钮组 → 光标移到按钮上可点击 → 移开恢复穿透;解锁态可按住拖动窗口;锁定/解锁切换正常