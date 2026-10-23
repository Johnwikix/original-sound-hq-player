# Last.fm 裁剪与 NativeAOT 回归

链接生产核心代码和真实 sqlite-net/SQLitePCLRaw，保留与主程序相同的 SQLite 程序集根配置，启用 Runtime Async，关闭 JSON 反射回退。夹具使用虚构凭据、独立数据库及异步 HTTP 桩，不访问真实 Last.fm 账号。

```powershell
dotnet publish _tools/LastFmAotRegression/LastFmAotRegression.csproj -c Release -r win-x64 -o artifacts/lastfm-fixed-trimmed
artifacts/lastfm-fixed-trimmed/LastFmAotRegression.exe
dotnet publish _tools/LastFmAotRegression/LastFmAotRegression.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/lastfm-fixed-aot
artifacts/lastfm-fixed-aot/LastFmAotRegression.exe
```

两种产物均需通过全部检查。覆盖旧 JSON 迁移、源生成 JSON、凭据图、SQLite 队列/预留/收据/时间别名映射、历史表 501 行属性映射、异步分页取消和服务导入。另链接生产 API 响应回归，验证数字/字符串成功数和拒绝码、配额、授权错误及畸形响应不会误删队列或冒充存储错误。

这验证 Last.fm 核心的可裁剪/AOT 兼容性；主 WinUI 程序使用其现有发布方式，并由 `_tools/LastFmUiRegression` 验证实际 UI 绑定。
