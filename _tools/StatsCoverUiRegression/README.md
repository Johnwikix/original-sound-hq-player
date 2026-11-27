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

## 听歌报告与 PNG 导出

同一回归工程会从生产统计页进入报告，切换年度和月度、快速选期、检查空期间与最近记录入口，然后使用实际 WinUI 海报控件捕获并编码 PNG。文件选择器的返回值由回归提供，覆盖取消、无效路径重试和已有文件替换。PNG 输出到 `artifacts/listening-reports-*/listening-report.png`，回归会重新解码检查尺寸和画面四角。

查询使用独立 SQLite，覆盖半开区间、闰日、本地时区的长短日、无效及重复零点、旧曲库身份、空期间与查询取消。压力场景插入 24 万条记录，每首歌和歌手各不相同，时间戳与写入顺序交错。输出预热后的八次查询耗时及调用线程托管分配，以及单次 PNG 保存过程中全进程的托管分配变化和私有字节结束值变化。这些值属于隔离 WinUI 回归进程。
