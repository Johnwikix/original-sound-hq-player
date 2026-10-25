# Last.fm 裁剪与 NativeAOT 回归

链接生产核心代码和真实 sqlite-net/SQLitePCLRaw，保留与主程序相同的 SQLite 程序集根配置，启用 Runtime Async，关闭 JSON 反射回退。夹具使用虚构凭据、独立数据库及异步 HTTP 桩，不访问真实 Last.fm 账号。

```powershell
dotnet publish _tools/LastFmAotRegression/LastFmAotRegression.csproj -c Release -r win-x64 -o artifacts/lastfm-fixed-trimmed
artifacts/lastfm-fixed-trimmed/LastFmAotRegression.exe
dotnet publish _tools/LastFmAotRegression/LastFmAotRegression.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts/lastfm-fixed-aot
artifacts/lastfm-fixed-aot/LastFmAotRegression.exe
```

两种产物均需通过全部检查。覆盖旧 JSON 迁移、源生成 JSON、凭据图、SQLite 队列/预留/收据/时间别名映射、历史表 501 行属性映射、异步分页取消和服务导入。另链接生产 API 响应回归，验证数字/字符串成功数和拒绝码、配额、授权错误及畸形响应不会误删队列或冒充存储错误。

另链接 `FavoriteRegression`，使用真实 SQLite 收藏表/队列测试：本地修改与待上报意图原子提交、512 行分页、相同操作合并、旧响应与新状态竞争、离线/重启恢复、成功后响应丢失的状态确认、数字/字符串收藏状态、会话失效后重连、换号期间已接受的旧账号操作隔离、解绑清理及退出写入屏障。

这验证 Last.fm 核心的可裁剪/AOT 兼容性；主 WinUI 程序使用其现有发布方式，并由 `_tools/LastFmUiRegression` 验证实际 UI 绑定。

`FavoriteFixRegression` 同时链接到两种发布产物：验证双向错误重试公平性（使用内部短退避策略，公开构造函数的生产退避不变）、慢授权期间收藏/拖排序的写入序列、512 行批次与库读取/播放入队交错、跨页重复文件不覆盖新的取消收藏、导入 ID 上界、取消后重开两个数据库续报、解绑及正常退出中断。夹具不使用私有反射或真实账号。
