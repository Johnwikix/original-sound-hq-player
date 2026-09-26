# WebDAV 回归验证

使用项目当前 .NET SDK、FFmpeg DLL 和 ATL 版本，直接链接生产读取、缓存、桥接与封面代码。

```powershell
dotnet run --project _tools/WebDavRegression/WebDavRegression.csproj
```

基础确定性检查覆盖：Unicode 目录、来源路径边界、跨主机重定向移除凭据、Range 读取与预算、版本变化、阻塞读取取消、桥接跨窗口数据一致性、未知令牌拒绝、播放期间开启缓存、完整缓存零上游请求、活动文件清理、无 Range 顺序降级、部分文件清理、开关反复切换与完整文件保留、弱 ETag 拒绝持久缓存，以及自研 Opus 跨页封面读取和预算。

2026-09-26：新增目录/下载资源的 ETag 和修改时间独立性、下载版本/时间变化及长度变化拒绝、直链缺 ETag 仍可 Range 定位、无 Range 重定向的顺序降级、无法映射目录版本的直链不发布持久音频缓存。目录 ETag/修改时间仍是索引身份，GET 版本在单次读取中独立校验；不保存签名 URL。

现增加缓存位置及 HTTPS/表单检查，共 50 项：本地真实 TLS 自签名/名称不匹配、证书确认后目录与 Range、跨来源和严格连接池隔离、证书变化/过期、确认保存恢复、SQLite 新字段迁移、修改地址与关闭时取消。
TLS 测试生成临时私钥，不向系统信任库添加证书；需要正常 Windows 用户的密钥存储权限。
表单测试链接生产 ViewModel 和传输，只替换应用数据库/凭据库服务边界；SQLite 模型另做真实内存数据库验证，不能替代 WinUI 实机交互验证。

实际服务验证通过当前进程环境变量提供连接信息；不要把密码写入源码、日志或提交文件：

```powershell
$env:MUSIC_WEBDAV_URL = 'http://127.0.0.1:5244/dav/'
$env:MUSIC_WEBDAV_USER = '<user>'
$env:MUSIC_WEBDAV_PASSWORD = '<password>'
dotnet run --project _tools/WebDavRegression/WebDavRegression.csproj -- --scan-all
```

仅诊断连接使用 `--connection-only`，输出固定错误码、HTTP 状态或目录项数量，不输出密码或文件名。
证书未通过时输出 SHA-256；核对后可通过当前进程环境变量 `MUSIC_WEBDAV_CERTIFICATE_SHA256` 显式指定该指纹，测试仅对当前来源生效。此诊断入口不自动信任首次遇到的证书，也不保存配置。

集成验证默认抽取根目录前两个文件，另包含指定的大封面 FLAC 回归样本（存在时）。`--scan-all` 递归验证音频元数据，输出成功/暂缓数量、读取音频字节、耗时、托管分配总量、GC 次数和运行时报告的累计暂停。这个入口不含主程序的数据库写入、列表更新、图像解码或 AudioPlayer；不能将其指标当作主界面性能或 NAS/WAN 性能。

`--integration-only` 跳过本地 fixture，只运行真实服务验证。可选 `MUSIC_WEBDAV_PLAYER` 指向带 FFmpeg DLL 的 AudioPlayer.exe：对抽样歌曲使用独立 IPC 命名空间，验证生产播放桥接能由实际播放器准备 PCM 并定位到 30 秒，随后停止会话和进程，不启动可听播放。

2026-09-22：真实 OpenList 224 首全部成功。独立播放器的设备播放/TLS/定位等验证使用 `_tools/StreamingRegression`；主程序界面、数据库迁移和凭据保存另做实际安装包验证。所有临时缓存使用系统临时目录内的独立随机子目录，不触及用户音频文件。
