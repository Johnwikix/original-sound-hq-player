# 单实例后台回归

```powershell
dotnet publish _tools/SingleInstanceRegression/SingleInstanceRegression.csproj -c Release -p:PublishDir=G:/SoftwareProject/winui/music_player/artifacts/single-instance-20261115/fixture-publish/ -p:PublishTrimmed=true -p:NuGetAudit=false --disable-build-servers -maxcpucount:2
& _tools/SingleInstanceRegression/Run.ps1
& _tools/SingleInstanceRegression/Run.ps1 -Packaged
```

链接生产互斥量/管道、文件参数解析与类型表、ShellService、TrayViewModel、窗口恢复和生命周期；采用与主程序相同的 Satori GC、ReadyToRun 和 Release 剪裁。每次使用随机名称，不读写用户曲库、不启动音频进程。

`-Packaged` 通过 Invoke-CommandInDesktopPackage 为测试 EXE 授予当前开发 AppX 的包身份，保留身份给子进程。不会启动播放器或改动包注册；逐个断言主/子进程身份，覆盖上次未打包测试遗漏的环境边界。测试窗位于屏幕外、不进入任务栏，最终前台激活调用被记录替换，不抢焦点。

覆盖：UI 未绑定时接收并确认、初始文件与中文/空格路径、连续/并发第二次启动始终保留原主进程、分批 UI 投递、普通启动不误播、超长请求与处理器异常、托盘隐藏/最小化恢复、退出期间拒绝新实例、停止后接管及崩溃遗弃互斥量恢复。

结果在 `artifacts/single-instance-20261115/{packaged,unpackaged}-*/result.txt`。此测试不点击真实开始菜单，不启动真实播放器 UI，不验证 Windows 最终前台权限；不能把它描述为完整主程序的交互验证。
