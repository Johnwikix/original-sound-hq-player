# 统计页封面回归

使用生产 `StatsPage.xaml(.cs)`、`StatsViewModel`、全部统计模型、`PlaybackStatsService`、`AlbumCoverBehavior`、`CoverLoadQueue`，以及真实 WinUI 调度队列、隔离 SQLite、WIC 解码与独立 `SoftwareBitmapSource`。

替身只提供曲库发布边界、无关播放入口和异步原始 PNG 读取；不会打开、迁移或改写用户曲库，也不访问在线封面服务。窗口在屏幕外以不激活方式显示，结果写入发布目录的 `result.txt`。测试数据库与图片位于该目录的 `artifacts`。

```powershell
dotnet publish _tools/StatsCoverUiRegression/StatsCoverUiRegression.csproj -c Release -r win-x64 -p:PublishTrimmed=true -p:TrimMode=link -p:SelfContained=true -o artifacts/stats-cover-ui/fixed
Start-Process artifacts/stats-cover-ui/fixed/StatsCoverUiRegression.exe -WindowStyle Hidden -Wait
Get-Content artifacts/stats-cover-ui/fixed/result.txt
```

覆盖历史 ID 已删除且曲库晚到、同专辑/艺术家回退、有效历史 ID 优先、歌曲行通知、相同引用不重载、共享像素但独立图片源、在途替换取消及迟到结果、删除清空、无对应分组、切换时间范围复用行、离页解绑和重进重新解析。底层原始读取门保留真实异步等待与取消，源创建与属性通知均发生在 UI 线程。

传入 `-p:StatsViewModelSource=<修改前文件的绝对路径>` 并使用另一个全新发布目录，可运行同一回归验证修改前的缺陷；预期在“deleted history anchors fall back ...”断言失败。

本回归不启动完整播放器、音频进程或在线封面下载；主程序仍需另外执行全新目录 Release/x64 剪裁发布。
