# Jellyfin 与 Navidrome 回归

从仓库根目录运行以下命令。测试链接生产协议、SQLite 曲库写入和迁移代码，使用隔离数据库和固定 HTTP 响应。

```powershell
dotnet run --project _tools/RemoteCatalogRegression -c Release
```

验证真实服务时，将测试账号信息放入当前进程的 `REMOTE_CATALOG_URL`、`REMOTE_CATALOG_USER`、`REMOTE_CATALOG_PASSWORD`，传入 `jellyfin` 或 `navidrome`。测试覆盖认证、目录及重复同步、封面、FLAC 和 MP3 的原始字节定位。测试服务各需要一首 FLAC、一首 MP3 和一张曲库封面。

```powershell
dotnet run --project _tools/RemoteCatalogRegression -c Release -- jellyfin
```

如设置 `REMOTE_CATALOG_PLAYER_PATH` 为带 FFmpeg DLL 的发布版 AudioPlayer.exe，测试也通过独立 IPC 运行实际解码、播放和定位。输出只包含检查结果、错误类型及汇总数字。完整 WinUI 交互及凭据存储另在应用包验证。

## 自包含发布包的 Jellyfin 连接检查

传入已生成的 AppX 目录或解包后的 MSIX 目录。脚本复制发布文件到临时目录，使用包内的 CoreCLR、BCL、`deps.json` 和 `runtimeconfig.json` 执行生产 Jellyfin 登录与目录探测，并核对运行时 DLL 的加载位置。默认使用内存 HTTP 响应。需要真实连接时，可设置以上三个 `REMOTE_CATALOG_*` 变量并传入 `-Live`。

```powershell
./_tools/RemoteCatalogRegression/RunPublishedJellyfinProbe.ps1 -AppDirectory <AppX目录>
```
