# WebDAV 保存响应回归

运行 `./Run.ps1`。独立的屏外 WinUI 窗口直接链接生产 `WebDavLibraryService.Sources.cs`，使用真实 DispatcherQueue、同步上下文及 PasswordVault。仅写入本次随机 CredentialKey 对应的测试密码，结束时删除，不读取用户来源或曲库。

数据库、曲库刷新和探活使用适配器：数据库入口含固定 300 ms 的同步延迟，防止仅因机器/凭据库已热身而掩盖 UI 阻塞；提交门控保留在途保存期间退出的异步时序。检查保存入口线程、UI 定时器最大间隔、新增来源避免整库刷新、编辑来源可见性刷新、通知回到 UI 线程、退出等待持久化且不发布迟到通知、保存失败仍可清理。

2026-09-26：修复前保存入口耗时 1571.6 ms、最大 UI 定时器间隔 1580.0 ms；修复后入口耗时 8.0 ms、最大间隔 122.9 ms。此为隔离 WinUI 回归场景，不能作为用户整库扫描耗时或主程序所有布局操作的性能承诺。

## 真实 Jellyfin 与 Navidrome 来源服务

用 `-p:LiveCatalogService=true` 构建此宿主，会链接生产连接 ViewModel、来源服务、协议、SQLite 曲库和歌曲版本存储。WinUI DispatcherQueue 和 PasswordVault 使用真实实现，应用外壳与 WebDAV 原生标签读取使用夹具。

```powershell
dotnet build _tools/WebDavSaveUiRegression -c Release -p:Platform=x64 -p:LiveCatalogService=true --artifacts-path _tools/WebDavSaveUiRegression/bin/catalog-service
```

在主机进程为两个服务分别设置 `REMOTE_CATALOG_JELLYFIN_URL`、`REMOTE_CATALOG_JELLYFIN_USER`、`REMOTE_CATALOG_JELLYFIN_PASSWORD` 和对应的 `REMOTE_CATALOG_NAVIDROME_*` 变量，再启动构建输出目录的 `WebDavSaveUiRegression.exe`。请使用专用测试账号与含封面的 FLAC 曲目。本测试会先故意验证一次错误密码，随后用正确密码恢复。

结果写入可执行文件旁的 `catalog-service-result.txt`。检查覆盖错误密码拒绝保存、修正与同步、三种来源共用曲库、通过来源解析音频和封面、关闭后重开和重复同步、停用及移除。测试只写随机临时曲库和新建的随机凭据键，结束时清理。

只验证保存并扫描时，设置 `REMOTE_CATALOG_LOCAL_SAVE_AND_SCAN=1`。此模式使用隔离 SQLite 和代码内的本机 WebDAV 服务器，覆盖连接表单释放后提交、数据库失败补偿、初次扫描失败，以及服务恢复后从已保存的来源重试扫描。无需服务器账号。

仅检查 Jellyfin 封面时，设置 `REMOTE_CATALOG_JELLYFIN_COVER_ONLY=1`，再用 `REMOTE_CATALOG_JELLYFIN_MISSING_IMAGE_ID` 提供专辑封面返回 404 且专辑中还有其他歌曲的曲目 ID，用 `REMOTE_CATALOG_JELLYFIN_NO_IMAGE_ITEM_ID` 提供无封面候选的曲目 ID。此模式只连接 Jellyfin，使用内置字节检查匹配结果的缓存与隔离。

验证 Jellyfin 补图后的同步时，设置 `REMOTE_CATALOG_JELLYFIN_RESTORED_COVER_ID` 为已补上自身图片的曲目 ID。如果该曲目原先回退到缺图专辑，再设置 `REMOTE_CATALOG_JELLYFIN_PREVIOUS_ALBUM_ID`。此模式使用隔离曲库和旧缓存夹具，检查同步保留曲目 ID、更换封面键并从 Jellyfin 读取新图。
