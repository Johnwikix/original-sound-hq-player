# 生命周期与系统入口回归

运行：`dotnet run --project _tools/LifecycleRegression --no-restore`。

项目链接实际 `AppLifecycle`、`ShutdownCoordinator`、`PlaybackCommands`、`TrayViewModel`、`LibraryWatcherService`。只替换窗口、音频、数据库等边界，不读写用户数据库或协议记录。

覆盖：

- 未就绪退出不保存播放状态、不停止尚未启动完成的 Host；正常退出保存一次，逆序清理，某项失败继续清理。
- 并发退出仅一个调用者执行；开始退出后不能恢复 Ready；迟到资源仍被清理。
- 托盘构造不创建播放后端；未就绪仍可显示窗口和退出，设置和桌面歌词不可执行；就绪/退出时命令状态更新。
- 明确 Play/Pause 不错误反转状态；不同入口的并发 toggle 被合并；上一首循环、seek 边界和播放列表变化通知。
- 真实临时目录：构造无 IO，重复启动不增加监视器，文件变化触发扫描，停止后不再扫描。

实际 Shell 图标注册仍须通过 `_tools/StartupSmoke/Assert-Tray.ps1` 在交互式桌面验证，不能用这里的 ViewModel 测试替代 WinUI/XAML 集成测试。
