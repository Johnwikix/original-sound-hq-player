# 禁用缓存后的曲库位置恢复

日期：2026-10-31。继续保持全部 Frame.CacheSize=0、页面 NavigationCacheMode.Disabled，保留已有分组详情进入状态。

## 最终行为

- 歌曲、最爱分别恢复最近的可见行及其视口内偏移。
- 专辑、艺术家、文件夹分别保存 SemanticZoom 当前层级、分组网格和缩略索引的位置。
- 当前分组的 MusicGroupDetailControl 列表也恢复位置；从详情返回后，原网格位置保留。
- 不自动定位当前播放曲目，不修改选择或播放状态。手动定位、鼠标/滚轮/按键及主动缩放切换优先于后台恢复。
- 保存稳定条目标识、索引、DIP 坐标及查询/分组身份；数据重排时按标识查找，条目删除后夹限至有效位置，空集合等待实际数据到达。
- 不同查询或不同分组不套用旧位置。每个页签只保存最近一次状态，不为所有访问过的分组或查询建无限增长的历史。
- 这是会话内恢复，重启程序后不保留位置。

## 实现与生命周期

BrowseState.Positions 是固定五个页签的纯值状态，内含三个视图槽及 SemanticZoom 布尔值。快照是 readonly record struct，保存字符串、整数和浮点值，不保存 Page、控件、集合、Music 对象或异步任务。

BrowsePositionKeeper 由页面拥有。页签导航在 OnNavigatedFrom 提前保存；Unloaded 不再用已脱离树的容器重新计算坐标，外层卸载沿用滚动快照。原生虚拟化容器就绪后，用实际条目坐标校正滚动，等待有限次布局，不改变虚拟化策略。

共享的弱表仅用于让 ListViewCentering 找到同一列表的恢复任务；卸载时主动移除。Loaded、VectorChanged、SizeChanged、ScrollViewer 和 SemanticZoom 等订阅均有明确解绑，恢复任务取消后观察异常并结束。相同实例重载也有代次保护。

## 实际验证

| 项目 | 结果 |
| --- | --- |
| 主项目 x64 Debug | 构建成功，0 错误，379 个编译/工具链警告。[日志](G:/SoftwareProject/winui/music_player/artifacts/browse-position/build.log) |
| 原生 WinUI/lvt 回归 | 150 项通过，13 次后台 UIA Invoke。[结果与测量](G:/SoftwareProject/winui/music_player/artifacts/browse-position/result.json) |
| 歌曲/最爱重建 | 不复用旧 Page，恢复前后可见条目坐标误差 0 DIP |
| 专辑/艺术家/文件夹 | 分组网格、缩略索引、缩放层级、当前分组详情列表及返回网格均通过；位置样本误差 0 DIP |
| 数据边界 | 重排后按身份恢复；空集合延迟填充后恢复；删除锚点回退；改变查询上下文回到顶部 |
| 交互优先级与取消 | 既有手动定位、快速连续请求、离页取消、数据源替换与重复队列身份回归通过；手动定位会中断同列表恢复 |
| 共享状态 | 既有 SharedStateRegression 通过，BrowseState 新纯值类型纳入构建。[日志](G:/SoftwareProject/winui/music_player/artifacts/browse-position/shared-state.log) |
| 页面回收 | 独立无 UIA 进程，三轮进入/退出曲库，每轮五个页签往返四次；跟踪 66 个页面，完整诊断 GC 后存活 0、终结哨兵确认回收 66。[原始记录](G:/SoftwareProject/winui/music_player/artifacts/frame-cache/after-position/memory.json) |
| 前台隔离 | 独立屏幕外 Show(false) / WS_EX_NOACTIVATE 窗口，只通过 lvt dump/invoke 操作；UI 与回收夹具监测前台激活违规 0 |
| 静态验证 | 四个生产 Frame 继续 CacheSize=0，无 Enabled/Required 页面；XML、原 BOM、CRLF、资源键与 git diff --check 通过 |

已查看 [专辑返回网格的实际原生截图](G:/SoftwareProject/winui/music_player/artifacts/browse-position/album-restored.png)。虚拟行使用变高 44/76 DIP，分组网格有 960 个条目和 120 个真实 CollectionViewSource 分组；位置断言容差 1.5 DIP，本轮样本均为 0。

测试链接实际五个页面的代码后置、歌曲/收藏 XAML、浏览页代码后置、位置状态、恢复/定位组件和导航边界。分组页 XAML 保留原生 SemanticZoom、分组 GridView、CollectionGroups 和详情 ListView 结构；卡片与详情编辑 UI、三个分组业务 ViewModel、播放器和磁盘服务使用适配器。它验证真实 WinUI 虚拟化、布局、生命周期与线程时序，同时避免操作用户数据。完整主程序的真实曲库、设备/DPI 组合及分组业务尚未逐一做前台交互验证，构建不能替代这些设备覆盖。

66 页实验针对页面可达性。因为数据和夹具范围较上一批扩大，没有用本轮 Private Bytes 与旧夹具比较，也没有宣称固定内存降幅或零分配。生产代码没有添加 GC.Collect。

## 复跑

~~~powershell
./_tools/ManualLocationUiRegression/Run.ps1
./_tools/ManualLocationUiRegression/Run-CacheMemory.ps1 -RunName after-position
~~~

回收脚本的诊断 GC 仅用于测试。功能记录统一追加到 docs/Changelog.md。
