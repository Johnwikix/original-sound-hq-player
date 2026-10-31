# 圆角往返修复与禁用导航缓存试验

日期：2026-10-31。变更基线：dee9a9fa。保留 Frame 和官方动画，不修改 CsWinRT。

## 点击后圆角丢失

独立后台 WinUI 测试复现了同一症状：点击推荐栏中间两首歌进入属性页，再返回，两张 160×160 封面仍然 IsLoaded，但根 Visual.Clip 从 CompositionGeometricClip 变为 null；没有触发重新加载或尺寸变化。

原先圆角裁切挂在连接动画的源/目标根 Visual 上。修复将裁切移到 MusicCoverControl 内部 CoverPresentation 的私有 Visual，动画依然以整个 MusicCoverControl 为源/目标。去掉原来额外的 XAML 矩形裁切，避免它与同一层的合成裁切争用。

相同往返测试在修改前失败，修改后两张卡片在进入前、进入后、返回后三个阶段均保留圆角。已查看真实 PrintWindow 渲染，保留原生 ConnectedAnimation.Completed 验证及官方动画。

- [修改前的裁切记录](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/rounded-before/roundtrip-clips.json)
- [修改后的裁切记录](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/rounded-after/roundtrip-clips.json)
- [返回后的实际截图](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/rounded-after/home-polish-roundtrip.png)

## 本轮缓存策略

全部四个生产 Frame：ShellFrame、MainFrame、PlayingFrame、曲库 ContentFrame，均显式 CacheSize=0。曲库外页原 Required，歌曲、收藏、专辑、艺术家、文件夹原 Enabled，本轮全部改为 Disabled；其他页面原本就是 Disabled，主壳与播放详情也补充显式 Disabled。

只设置 CacheSize=0 不足以覆盖 Required，因此必须同时修改页面属性。[微软页面缓存说明](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.page.navigationcachemode)区分了 Required、Enabled 和 Disabled 的行为。

本轮试用的行为变化：

- 离开再进入会创建新的页面对象，保留 ViewModel 中已有的页签、筛选和排序状态。
- 没有引入滚动快照状态。纯视图的滚动位置返回顶部；仍通过手动按钮定位当前曲目。
- Frame 当前 Content 仍属于正在显示的视图；禁用导航缓存不会清空当前内容。
- MainPage、PlayingDetailPage 仍由现有 DI 单例持有，播放详情退出仍使用原来的暂停/隐藏流程。因此本轮不声称会销毁它们，也不改变播放器和播放状态生命周期。

## 验证结果

| 检查 | 结果 |
| --- | --- |
| 主项目 x64 Debug | 0 错误；构建存在 515 个编译/工具链警告。[日志](G:/SoftwareProject/winui/music_player/artifacts/frame-cache/main-build.log)。 |
| 首页原生/lvt 回归 | 54 项通过、13 次 lvt Invoke；含两张卡片属性往返、缺图动画、预览进入属性页、原生动画完成及资源生命周期。[结果](G:/SoftwareProject/winui/music_player/artifacts/home-polish-batch2/rounded-after/result.json)。 |
| 列表与导航原生/lvt 回归 | 105 项通过、12 次 lvt Invoke；确认重建歌曲页、外层曲库重建、跨页专辑/艺术家入口、初始化前选页、页签高亮、取消和手动居中。[结果](G:/SoftwareProject/winui/music_player/_tools/ManualLocationUiRegression/bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/artifacts/result.json)。 |
| 既有完整首页回归 | 184 项通过。[结果](G:/SoftwareProject/winui/music_player/artifacts/frame-cache/home-regression.txt)。 |
| 前台隔离 | 独立进程、屏幕外 Show(false)、WS_EX_NOACTIVATE。lvt 仅 dump/invoke，未使用真实鼠标键盘；lvt 和回收夹具每 10ms 监测，前台违规 0。 |
| 静态检查 | 四个 Frame 均 CacheSize=0，无 Enabled/Required 页面；XML、原 BOM、CRLF、七语言 GetString 键及 git diff --check 检查通过。 |

列表夹具编译真实歌曲/收藏页面、曲库代码后置、ViewModel、导航边界和原生 Frame。其他分组页、行内容、播放器及业务依赖采用适配器。完整生产应用中所有分组详情和播放详情未逐个手动操作；未执行 AOT 发布。

## 实际回收与内存

无 UIA 观察者的两个独立后台进程，各执行三轮曲库进入/退出，每轮歌曲与收藏往返四次。退出曲库后使用三次仅存在于测试的诊断 GC，并等待终结器和 UI 队列。WeakReference 和 ConditionalWeakTable 终结哨兵同时计数。

| 最终指标 | 修改前缓存开启 | 本轮禁用缓存 |
| --- | ---: | ---: |
| 实际创建的独立曲库/歌曲/收藏页面 | 3 | 27 |
| 离开曲库后仍存活 | 3 | 0 |
| 终结哨兵确认回收 | 0 | 27 |
| 托管内存 | 1.17 MiB | 1.41 MiB |
| 进程 Private Bytes | 119.70 MiB | 170.78 MiB |
| 句柄 | 1154 | 1160 |

[修改前记录](G:/SoftwareProject/winui/music_player/artifacts/frame-cache/before/memory.json)、[禁用后记录](G:/SoftwareProject/winui/music_player/artifacts/frame-cache/after/memory.json)。MiB=1,048,576 字节。修改前三个实际页面源文件快照保存在 artifacts/frame-cache/before-sources，未回滚生产文件做实验。

结论：本轮已经证明旧页面可以回收，避免 Frame 缓存持续保留这些页面；但这个短场景的总私有内存没有下降，反而较高。每次重新创建页面也会产生更多分配。本次没有将差异进一步拆分到 WinUI 原生分配器、共享资源缓存或内存高水位，因此不把这些机制当成已经证实的根因，不承诺整体内存或显存收益。

本表不是修改前后同数量页面对象的比较，而是同样导航次数下，缓存复用与页面重建两种策略的真实差别；不能把“可回收”直接当作“工作集下降”。没有测量主程序真实曲库的长期驻留、页面重建延迟或显存。代码按本轮尝试要求保持全部导航缓存禁用。

复跑当前策略：

~~~powershell
./_tools/ManualLocationUiRegression/Run-CacheMemory.ps1
./_tools/ManualLocationUiRegression/Run.ps1
./_tools/HomeUiRegression/Run-Polish.ps1 -RunName rounded-replay
~~~

原始失败记录来自 Run-Polish -Baseline，该模式容忍预期失败以完成采集，其末尾脚本结束信息不代表断言全部通过；应查看 state/result 中的 Failures 与 roundtrip-clips。修改后的普通模式要求所有断言通过。
