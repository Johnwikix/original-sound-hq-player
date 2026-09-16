# Windows 启动/托盘实机检查

在有交互式桌面的 PowerShell 7 中运行；先部署当前开发包，避免只 build 后运行旧的 AppX 文件。

已同意协议并完成启动时：

```powershell
$player = Get-Process -Name 'OriginalSound HIFI Player'
./_tools/StartupSmoke/Assert-Tray.ps1 -PlayerProcessId $player.Id
```

协议弹窗尚未同意时也必须已注册托盘：

```powershell
./_tools/StartupSmoke/Assert-Tray.ps1 -PlayerProcessId $player.Id
```

脚本只读，用 `Shell_NotifyIconGetRect` 查询 Explorer 中的真实注册，不根据日志猜测，也不修改协议记录。GUID 算法与项目使用的 H.NotifyIcon 2.4.1 一致；升级该依赖或主动配置图标 ID 时同步调整探针。

完整检查矩阵与复位位置见 [启动文档](../../docs/LegalAndStartup.md)。需另外操作托盘菜单、窗口隐藏/恢复和退出；仅有图标注册成功不能证明所有菜单命令正确。Shell API 返回的矩形不能直接假定为鼠标注入工具的坐标系，自动化脚本还必须检查输入注入是否成功。

协议期间还应检查：显示窗口/退出可用，播放/设置及各功能子菜单禁用；无音频子进程、无协议同意记录。`-ExpectedAbsent` 仅用于预期尚未创建或已经移除图标的诊断场景。
