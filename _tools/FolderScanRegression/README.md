# FolderScanRegression

从仓库根目录运行。项目链接生产扫描核心、SQLite 扫描 partial、AddFolderViewModel、FolderCommands 和 FFmpeg 探测器；只替换平台依赖，不连接用户数据库。

```powershell
dotnet run --project _tools/FolderScanRegression --disable-build-servers
```

FFmpeg 用例需要先构建主项目的 Debug x64 输出。临时音频和 SQLite 库在独立临时目录创建并清理。`-- --legacy` 是负向重现：应抛出慢文件阻塞全部批次的断言错误。

若 NuGet 的全局临时锁目录不可写，可以只为当前 PowerShell 进程指定临时目录，并禁用构建服务器：

```powershell
$env:NUGET_SCRATCH = Join-Path $env:TEMP 'codex-folder-scan-nuget'
dotnet restore _tools/FolderScanRegression --disable-build-servers -p:NuGetAudit=false -nr:false
```

调度分配测量使用合成输入，不代表实际音频扫描吞吐、完整应用内存或 WinUI 帧时间。

标签延迟写入回归链接生产队列代码，使用真实 SQLite 和 Windows 文件共享锁，覆盖占用/释放、快照、重复保存、重新打开数据库、扫描保护及失败恢复。标签写入器用文本写入替身隔离，因此不验证 ATL 各音频格式或真实播放器的句柄释放时机。
