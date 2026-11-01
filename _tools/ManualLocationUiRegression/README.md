# 手动定位与导航引用清理：第一批验证报告

本批保留 Frame 与官方进入/左右切页动画。HomePage 显式设置 `NavigationCacheMode.Disabled`，其他页面的缓存策略沿用原值；不增加统一活跃页状态或延迟刷新队列，也不改变播放详情渲染的启停条件。

实现内容：

- 歌曲、收藏、专辑/艺术家/文件夹详情、歌单详情，以及底部播放栏和播放详情中的两个队列入口，均增加手动定位按钮。进入页面、切歌和打开队列不再主动滚动。
- ViewModel 在当前显示集合中查找目标并更新选择，视图转发按钮输入并负责滚动；目标不在筛选结果中时禁用按钮，执行入口仍做空目标守卫。
- 列表等待虚拟化容器生成后按实际行坐标居中，首尾夹到滚动边界。重复请求、集合变化和卸载会取消未完成操作；歌曲/收藏页在 OnNavigatedFrom 提前取消。
- 队列沿用 `GetSelectedPlaybackIndex()` 的真实条目索引，保留重复入队和待播放选择的语义。无法直接生成重复对象的指定索引容器时，先估算滚动位置，再按实际容器精确居中。按钮绑定同时观察已有的 `Queue.Version`，不新增状态。
- 移除单例 ViewModel 对浏览页、歌曲页、收藏页、详情控件和 SelectorBarItem 的引用。页签只保存字符串；导航边界延迟取得应用外壳，不缓存子页面。详情 ViewModel 原有订阅改用成对 Activate/Dispose 管理。
- 移除旧 ScrollerHelper 定时器辅助类。新的定位器只在操作期间订阅卸载和集合事件，并在 finally 中解绑。
- 七语言补齐按钮提示与无障碍名称，使用 [MRTCore 文档规定的附加属性资源键](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/mrtcore/localize-strings#refer-to-a-string-resource-identifier-from-xaml)，没有新增 GetString 调用。

验证结果（2026-10-30）：

| 项目 | 结果 |
| --- | --- |
| 主项目 x64 Debug 构建 | 通过，0 错误；仍有编译/工具链警告，详见构建日志 |
| 独立 WinUI 回归 | 93 项检查全部通过 |
| lvt 实际按钮调用 | 12 次，全部成功，包括 3 次真实定位按钮操作（歌曲页两次、收藏页一次）和测试窗口退出 |
| 1200 条变高行、远距离目标与快速重复请求 | 居中检查通过，非首尾样本实测最大偏差 0 DIP，断言容差 1.5 DIP |
| 同一 Music 对象出现在队列第 20、950 索引 | 选中第二个条目时正确定位到第 950 索引并居中 |
| 缓存页重入 | 位置保留，快速切页后手动定位仍可用，不需要定位事件订阅 |
| 边界与取消 | 首尾夹限、空目标、筛选缺失、等数量集合替换、新请求取消、集合重置取消、离页取消均通过 |
| 导航 | 五个页签、跨页专辑/艺术家入口、首次 Loaded 前选页及 SelectorBar 高亮同步通过 |
| 前台隔离 | 屏幕外窗口、Show(false)、WS_EX_NOACTIVATE；每 10 ms 检查一次，前台违规 0 次 |
| 本地化与格式 | 七语言新键齐全；实际歌曲页解析提示及无障碍名称成功；XML、CRLF 和 git diff --check 通过 |

测试链接实际生产代码：歌曲/收藏页 XAML 与代码后置、三个页面 ViewModel、MainViewModel、浏览页代码后置、LibraryNavigationService、PlaybackQueueState、BulkObservableCollection、定位器与查找辅助。浏览页工具栏简化为相同 SelectorBar 绑定与 Frame；专辑/艺术家/文件夹页面、封面行、播放/文件操作和其他服务使用适配器，避免启动播放器或操作用户数据。

本次没有在完整主程序中逐个打开详情页和两个 TeachingTip 验证最终外观；这些入口通过主项目 XAML/C# 构建，公共定位算法和队列游标通过独立真实 WinUI/队列状态测试。未重新量化整机内存/显存，也没有执行 NativeAOT 发布。解除 ViewModel 引用不会使 Frame 仍缓存的页面立即释放，因此本批不报告具体内存收益。

复跑（PowerShell，仓库根目录）：

```powershell
dotnet restore _tools/ManualLocationUiRegression/ManualLocationUiRegression.csproj -p:Platform=x64 --configfile _tools/ManualLocationUiRegression/NuGet.Config
& ./_tools/ManualLocationUiRegression/Run.ps1
```

测试窗口只通过 lvt UIA Invoke 操作，不使用激活、鼠标或键盘输入。结果位于本项目 `bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/artifacts/`：`result.json` 包含断言与坐标测量，各次 dump/invoke 日志及 PNG 也在该目录。生产构建与静态检查记录分别位于仓库 `artifacts/manual-location-build.log`、`artifacts/manual-location-static.json`。
