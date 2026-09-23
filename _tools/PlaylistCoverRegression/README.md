# 播放列表卡片封面回归

Windows x64 的独立 WinUI 测试程序，不读取或修改用户音乐库、设置及封面缓存。测试窗口放在屏幕外，结果写到可执行文件同目录的 `result.txt`，失败时退出码为 1。

## 问题与范围

1.2.5.0 的卡片通过 `PlayListCoverMusicConverter(Id)` 读取歌曲映射，绑定只监听不变的 ID。`47bc32a6` 为提前显示主界面而调整了启动顺序，卡片可能在映射加载前完成首次绑定，后续数据到达没有对应属性通知。

测试链接生产 `PlayListPage.xaml`、`PlayList`、`PlaylistSummaryProjection`、`LibraryQueries`、`LibraryState` 和 `AlbumCoverBehavior`，使用真实 WinUI 绑定、Dispatcher、可视树与异步图片加载时序。`Adapters.cs` 替代无关的应用服务/导航，以内存图片替代文件读取；不覆盖完整应用的 SQLite 读写、启动协调器或用户现有图片文件。

覆盖：

- 卡片先显示，歌曲库与映射异步到达；ID 和已有歌曲数量不变时也刷新图片。
- 空索引失效、失效歌曲映射、空歌单、新歌单。
- 歌曲数量不变的排序、音乐库删除、移除全部成员。
- UI 线程属性通知；SQLite 映射忽略 `CoverMusic`，无需数据库迁移。

## 构建与运行

在仓库根目录使用 PowerShell：

```powershell
dotnet build _tools/PlaylistCoverRegression/PlaylistCoverRegression.csproj -c Release
$coverTestExe = Join-Path $PWD '_tools/PlaylistCoverRegression/bin/Release/net11.0-windows10.0.26100.0/win-x64/PlaylistCoverRegression.exe'
$coverTest = Start-Process -FilePath $coverTestExe -WindowStyle Hidden -PassThru
if (-not $coverTest.WaitForExit(20000)) { $coverTest.Kill(); throw 'Test timed out' }
Get-Content (Join-Path (Split-Path $coverTestExe) 'result.txt')
if ($coverTest.ExitCode -ne 0) { throw "Test failed: $($coverTest.ExitCode)" }
```

## 旧页面对照

仅提取 1.2.5.0 的原始 XAML 到已排除的诊断目录，不回退工作区：

```powershell
New-Item -ItemType Directory -Force artifacts/playlist-cover-before | Out-Null
git show 1b0c40a0:View/PlayListPage.xaml | Set-Content -Encoding utf8 artifacts/playlist-cover-before/PlayListPage.xaml
$legacyPage = Join-Path $PWD 'artifacts/playlist-cover-before/PlayListPage.xaml'
dotnet build _tools/PlaylistCoverRegression/PlaylistCoverRegression.csproj -c Release --no-restore "-p:PlaylistPageXaml=$legacyPage" -p:IntermediateOutputPath=obj/Legacy/ -p:OutputPath=bin/Legacy/
```

用上面的运行步骤启动 `_tools/PlaylistCoverRegression/bin/Legacy/PlaylistCoverRegression.exe`。预期在 `late mapping updates existing card without changing Id or persisted SongCount` 断言失败、退出码为 1；默认生产页面通过。这项对照隔离的是绑定刷新缺陷，不等同于运行完整的旧版应用。

2026-09-23：旧页面对照按预期失败，修复后的全部场景通过。主程序默认输出路径及独立输出路径的 x64 Release 构建均为 0 错误；验证命令关闭了包签名与符号包，未修改发布默认值。
