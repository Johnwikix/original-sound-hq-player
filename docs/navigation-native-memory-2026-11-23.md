# WinUI 导航原生内存诊断

本轮生产源码基线为 `d0673b18e06a4f72625c58ed9a8e76ed9e8c4904`。该版本保留原转场，外层 Frame 中的曲库、目录、歌单和设置各复用一个页面。实验从未启动播放的同进程混合导航中观察剩余增长。设置内层和曲库内层须单独核对，四个外页的身份一致无法说明整个 XAML 子树的生命周期。

采集机为 Windows 26H2（26300.9974），.NET SDK 11.0.100，项目启用 Satori GC，Windows App SDK 2.7.0。官方构建解包与诊断发布中的以下 DLL 字节一致。文件版本与 NuGet 包版本分别记录。

| 模块 | 文件版本 | SHA-256 | PE 中的 PDB GUID＋Age |
| --- | --- | --- | --- |
| `Microsoft.UI.Xaml.dll` | 3.2.0.2511 | `0554F662E7ABFBF2472E3EAC4EAB296D15E9A6999EA11842378478E1665749E1` | `41eea12d41b0d03d61648abb28996270`＋2 |
| `Microsoft.UI.Xaml.Controls.dll` | 3.3.0.0 | `00F947B4FCF38E76283598688ACFAF6CCD39D8F3B28C0B953C1EB2281EE8FD03` | `eb99bab6a56538d281bfb4d0f26bc839`＋2 |

## 真实播放器进程堆采集

在独立私有桌面的 Release x64 裁剪发布中运行 6,600 首、500 首歌单、原转场和全混合导航，确认全程未播放，外层四页各一实例。采集进程 PID 14048。启动阶段已做过一整轮，xperf 在第二轮途中开始，完成第四轮后保持歌曲页，依次静置、诊断 GC 和静置 8 秒。本次 ETL 记录 57.86 秒、5,874,122,752 字节，QPC 频率 10 MHz，xperf 和两遍解析均报 0 丢失事件和缓冲区。开启堆栈跟踪显著拉长了路由耗时，此组用于归因。首个未预热采集达容量上限并丢缓冲区，未用于以下数字。

| 同一歌曲页时间点 | Private Bytes（MiB） | 默认堆内已观察到的在途分配（字节） |
| --- | ---: | ---: |
| 第 2 轮结束 | 472.15 | 22,961,860 |
| 第 3 轮结束 | 482.47 | 59,357,442 |
| 第 4 轮结束 | 487.34 | 59,222,017 |
| 诊断 GC 后再等 8 秒 | 498.21 | 60,027,003 |

这些在途分配均有可关联的分配栈。比较第 2 轮至 GC 后 8 秒，观测窗口内的默认堆在途增加 35.35 MiB。以最靠近分配器的非分配器模块做互斥汇总，DirectWrite 增 27.91 MiB，约占此增量的 79%，coreclr 增 4.58 MiB，XAML 增 1.25 MiB。按调用栈包含模块做重叠汇总，含 XAML 的栈增 33.87 MiB，含 Controls 的栈增 8.29 MiB。采集窗口开始前已经分配的块不在 ETW 队列中，旧块释放可以让整个堆忙字节下降。独立 Rtl 堆快照中，默认堆提交 269.24→284.62 MiB，枚举到的默认堆忙块 221.28→218.21 MiB。全部堆的提交 272.21→287.86 MiB，忙块 223.19→220.12 MiB。该差分与 ETW 在途增量使用不同口径。

解析用同一 PID、线程和原始 QPC 关联堆事件与栈。默认堆通过目标进程中的 GetProcessHeap 标识。已知块验证覆盖 229,461 字节分配、321,197 字节重分配及最终释放，观察到移动 realloc 先发新地址分配、旧地址释放，再发 realloc 汇总。一组事件在结尾只有一个在途块，按最后的 realloc 时间和分配栈分组。原地 realloc 同样结束旧逻辑记录、开始新记录。年龄由最后一次已观察到的分配或 realloc 决定；年龄分组无法跟踪逻辑对象跨 realloc 的历史。

实际窗口默认堆共 7,514,795 条相关堆事件。出现 6,439 次移动 realloc、6,439 次旧地址已不存在及 6,439 次已有新地址的替换。另有 91,170 次没有对应窗口内分配的 free，包含采集前的分配。无效事件形状和同地址时间戳冲突均为 0。此组只采 Alloc/Realloc 调用栈，同时消费 Free 事件以得到在途大小。[堆事件格式](https://learn.microsoft.com/en-us/windows/win32/memory/memory-management-tracing-events)

最重的 DirectWrite 栈在两个端点之间增加 2.34 MiB。用与 DWrite.dll 匹配的 Microsoft PDB `5F94F5E81DD8EC5D88E9C53E9B1E9AB61` 解析，符号链从分配端依次经过 `DWriteTextAnalyzer::GetGlyphs`、`TextLayout::GetTextGlyphs`、`TextLayout::GetGlyphs`、`TextLayout::SlowPath`、`TextLayout::AnalyzeText`、`TextLayout::TextLayout`、`MutableTextLayout::DoLayout` 和 `DWriteTextLayout::GetMetrics`。匹配的 XAML PDB 将其后的 `Microsoft.UI.Xaml.dll+0x46a29` 解析为 `CTextBlock::ArrangeOverride+0x169`，再经 `CFrameworkElement::ArrangeCore` 和 `CTextBlock::ArrangeCore`。此链路定位文本排列中的字形和布局分配。另一条单独增加 4.00 MiB 的 coreclr 原生分配栈经过 XAML 的 `TrackerTargetReference::ReferenceTrackerWalk` 与 SelectionChanged 事件源引用跟踪，属于不同的调用链。

第 3 轮内分配、在该轮末仍在途的 DirectWrite 共 37,212 块，34.98 MiB。按动作结束时标划分，最大区间为回歌曲页（7.00 MiB）、收藏（6.85 MiB）、歌单（5.41 MiB）、专辑（4.83 MiB）、目录（3.00 MiB）及歌手（2.04 MiB），多个设置动作也有分配。时标表示区间归属，无法判定异步排队工作的控件。每个已跟踪的第 3 轮 DirectWrite 逻辑块在第 4 轮歌曲页前已经退役，静置和 GC 后仍为 0。因此跨时间点的 DirectWrite 净增加来自新旧分配替换，不能用它估计第 3 轮有多少分配一直不释放。

同样跟踪第 3 轮出生且该轮末仍在途的全部默认堆分配。GC 后 8 秒剩余量占该批起始字节的 0.48%，约为此时默认堆枚举忙字节的 0.11%。DirectWrite 的 37,212 次退役全部有 op36 Free。

| 同一分配批次的观察点 | 仍在途块数 | 仍在途字节 |
| --- | ---: | ---: |
| 第 3 轮歌曲页 | 136,655 | 52,938,478 |
| 第 4 轮歌曲页 | 2,201 | 631,632 |
| 末轮静置 | 2,193 | 303,712 |
| 诊断 GC 后 | 1,994 | 256,437 |
| 诊断 GC 后再等 8 秒 | 1,990 | 254,133 |

最后仍在途的主要模块为 XAML（109,580 B）、coreclr（69,152 B）、dwmcorei（28,144 B）及 CoreUIComponents（20,160 B）。同分配栈、大小和出生区间的较大组如下。已识别符号来自匹配的 Microsoft PDB。

| 最早动作区间 | 数量 × 单块 | 分配调用链 |
| --- | ---: | --- |
| 收藏 | 1 × 19,200 B | dwmcorei `CSurfaceBrush::SetSurface` → `CResource::RegisterNotifier` → `CPtrArrayBase::InsertAt` |
| 设置回常规 | 65 × 144 B | XAML `VisualStateManagerActuator::InitializeStateTriggers` 中弱指针 vector 扩容 |
| 收藏 | 55 × 160 B | XAML `CDependencyProperty::CreateDefaultValueObject` → `CSetterBaseCollection::Create` |
| 快捷键 | 1 × 8,216 B | coreclr `+0xb2b4a` / `+0xb2649`，调用方经过 clrjit |
| DSP | 46 × 176 B | XAML `TryGetOrCreateStateTriggerVariantMapImpl` → `std::make_shared<VariantMap<...>>` |

归到播放器模块的是诊断发行版主 DLL `OriginalSound HIFI Player.dll` 的十块各 239 B。核对该二进制 SHA-256 `01BE44320AE5E26BC829C8A0CE8CD8D6D042EC19906645CE3E999CEB26763656` 与 ReadyToRun 方法表，九块的 `+0x178af6` 映射至诊断夹具 `IsolatedNavigationProbe` 的 `<RunAsync>b__49`，匹配的 Portable PDB 指到夹具第 316 行设置 Expander 的 `IsExpanded = false`，合计 2,151 B。此映射确认了触发动作，持有该分配的对象尚未识别。剩下一块的 `+0x409b6b` 未映射到具体业务方法。DWM SurfaceBrush 分配栈也缺少指向具体业务控件的持有证据。

## 终点释放对照

同一个诊断发布各运行一遍 12 轮。两边都在歌曲页等待并执行一次诊断 GC，再进入中性页。对照组保留四个外页缓存，释放组只在中性页完成后驱逐外层四页以及应用管理的相关内部内容。两组执行相同的终点等待和 GC。末尾调用 `HeapSetInformation(HeapOptimizeResources)` 只用于额外诊断默认堆，发生在下表主要终点之后。

| 端点或变化（MiB） | 保留 | 驱逐 |
| --- | ---: | ---: |
| 第 1 轮歌曲页 Private Bytes | 434.73 | 370.26 |
| 第 12 轮歌曲页 Private Bytes | 552.46 | 516.30 |
| 切到中性页，仍缓存时 Private Bytes | 557.40 | 517.70 |
| 中性页后到终点 GC，Private Bytes 变化 | −0.26 | −78.79 |
| 同阶段默认堆提交变化 | 0.00 | −5.51 |
| 同阶段默认堆已分配变化 | +0.31 | −72.49 |
| 同阶段默认堆枚举忙块变化 | +0.22 | −135.51 |
| 同阶段托管存活估计变化 | −1.43 | −12.71 |
| 终点 GC 后默认堆优化再释放的 Private Bytes | 26.77 | 10.75 |

对照组的八个原始页面包装在终点仍活。驱逐组八页都已卸载，六个原始包装的短弱句柄清空，曲库及当时的歌曲页包装仍可达。两组启动后内存基线不同，释放效果采用各自中性页到终点 GC 的差值。可释放容量与页面控制相关，现有对照不能据此指定某个控件为所有者。默认堆优化结果说明仍可释放部分提交容量。应用生产导航无此调用。

另做一轮所有权检查，当前曲库进入中性页后外层 BackStack 只有一项。保留附着状态清空该项，经等待、双 GC 及终结器，曲库和歌曲页的原始短弱句柄仍非空。此后将真实焦点移至中性页按钮，FocusManager 确认真正获得焦点，再执行相同等待和 GC，这两个包装仍在。本次检查排除了该外层历史项或当前焦点单独决定其存活。

另以同一诊断发布各运行 3 轮的短堆采集，按地址匹配同一批在末轮歌曲页仍活的 DirectWrite 分配。两条 ETL 均无事件或缓冲区丢失。保留组末轮的 34,150,310 B 到终点 GC 剩 34,079,492 B，匹配 Free 70,818 B（0.21%）。驱逐组末轮的 33,854,637 B 到终点 GC 剩 7,412,558 B，匹配 Free 26,442,079 B（78.1%）。两组在歌曲页静置及 GC 时均先释放 70,818 B，驱逐组其余 26,371,261 B 的释放发生在进入中性页、执行驱逐并走到最终 GC 后。两组起始字节排名前 16 的组均落在 16,384 B 的 GetGlyphs 栈。这些组在保留进程中维持 17.86 MiB，在驱逐进程中由 17.83 MiB 降至 6.23 MiB。这给出了同一批 DirectWrite 分配对页面生命周期的响应，也显示单纯将缓存页卸载并等待不会释放这批已保留的文本布局。

普通导航下一轮就释放绝大部分受观测分配，而强制驱逐可提前释放一部分当时的页面布局。现有数据支持保留已验证的有界复用，在保留原 UI 和重进性能的约束下，还未定位可以安全调整的业务资源所有者。若后续比较文本模板，保留同内容、尺寸、换行及页面路由，以固定动作前后的相同分配栈和实际未释放字节作为验收信号。

## 应用侧所有权

| 路径 | 基线的释放行为 | 能够检验的归因 |
| --- | --- | --- |
| [AutoScrollView](../Controls/AutoScrollView.cs) 和 [RedirectVisualView](../Controls/RedirectVisualView.cs) | 构造时创建合成视觉、VisualSurface、Brush 和动画。卸载时摘除 ChildVisual，清理表达式参数、源视觉和动画，并释放临时裁剪。构造时资源随控件一起复用。 | 对比每轮新增控件和附着状态，检查分配栈是否经过这一创建路径。若仍怀疑跨导航持有，比较相同路线下仅释放相关应用资源前后的在途分配。 |
| [FolderTreeMotion](../View/Controls/FolderTreeMotion.cs) 和 [FolderTreeAnimations](../View/Controls/FolderTreeAnimations.cs) | 页面卸载时结束运动、解绑事件，释放动画组及合成动画。 | 以目录路线单独运行、复用退出及重新加载为对照，区分可回收的每轮动画对象与仍在途的分配。 |
| [ConnectedAnimationScope](../Helper/ConnectedAnimationScope.cs) | 目标或宿主卸载时取消，清除目标卸载订阅。 | 需要分配和释放证据落到实际使用此帮助类的路由，才能归因。 |

Private Bytes 包括私有已提交页。比较时分开报告堆忙块、未忙但已提交的堆容量、托管堆提交及其他映射。GPU 资源须维持单独口径。看到 XAML 模块分配时，需要独立的持有或释放实验才能区分页面留存、框架内部缓存及分配器容量。释放块也可能只让堆可重用，Private Bytes 未必随即下降。

WinUI [#934](https://github.com/microsoft/microsoft-ui-xaml/issues/934) 的原始报告来自 2019 年的 UWP。它描述用 `Frame.Navigate` 与 `DrillInNavigationTransitionInfo` 往返含多个按钮的页面，页面终结后进程内存仍高。2024 年的跟帖提供了 Windows App SDK 1.5.240802000 的 WinUI 3、100 行 ListView 最小复现，并报告 GC 后仍留有原生内存。两者均没有可用于匹配本案的分配栈，现象可用于设计框架最小复现。

## 曲库页签和滚动文字合成资源对照

本组使用同一台机器、相同的 6,600 首合成曲库、500 首歌单和未播放的混合路线。A 为本次改造前的提交 3e76d93d，使用曲库内层 Frame 与构造即创建的滚动文字合成资源。B 采用 SwitchPresenter 和明确的页签激活生命周期。C0 在 B 的基础上将滚动文字合成资源改为按需创建。D 加入快速重入时对迟到卸载事件的修复，对应最终生产源码。外层 Frame 和原有入场转场、列表模板、虚拟化及数据投影保持一致。各版本独立编译 Release x64 裁剪诊断发布，在独立桌面以 1280×900 窗口和 450 ms 路由节拍运行。夹具核对目标页、数据和列表容器，确认窗口未取得前台。

Toolkit 8.2.251219 的 SwitchPresenter 在 Case 变化时读取 Case.Content。曲库在首次切换前为该 Case 创建页面，保留已经访问过的四个页面实例。曲库回归夹具通过 97 项检查，确认同一 UI 队列内连续切换后，只有最终页签留在视觉树中，程序化聚焦的 SelectorBarItem 保持焦点，外层离开后停止页签激活。 [Toolkit 8.2.251219 源码](https://github.com/CommunityToolkit/Windows/blob/v8.2.251219/components/Primitives/src/SwitchPresenter/SwitchPresenter.cs)

独立 Debug x64 WinUI 控件夹具检查构造后的合成对象。关闭滚动的长文本和开启滚动的短文本各从 10 个受检对象降至 0 个，第一次挂接溢出文本时创建这 10 个对象，两帧截图的像素变化确认发生滚动。实际歌曲行的曲名、专辑、艺术家三列在悬停前均无 VisualSurface，首次悬停只初始化目标列。夹具复现新 Loaded 之后收到旧 Unloaded，修复后重入时的滚动和合成源保持有效，真正卸载和回收时清理。

另用带封面的独立小库验证同一队列内歌曲页快速离开再返回。SwitchPresenter 的正式重复 18 次均显示新的 SoftwareBitmapSource，正常离树清除封面，迟到 I/O 未写回。早期一次超时未复现。旧 Frame 的 6 次检查均通过，夹具写出结果后在退出阶段崩溃。封面检查没有采集 GPU 字节。

### 常规导航

以每轮回到歌曲页的轮末为采样点。最后一轮还在歌曲页静置、执行诊断 GC，再等待 8 秒并枚举默认堆。表内存单位为 MiB。原始目录含 measurements.csv 和 ab-summary.json。

| 运行 | 第 1 轮 Private Bytes | 第 8 轮 | 第 12 轮 | GC 后再等 8 秒 | 默认堆提交 | 默认堆已分配 | 默认堆枚举忙块 | 托管堆提交 | 托管存活估计 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A1（a-normal1） | 425.86 | 546.87 | 586.82 | 586.30 | 313.63 | 295.56 | 230.20 | 156.73 | 36.42 |
| B1（b-normal1） | 441.63 | 558.07 | 566.07 | 569.14 | 318.11 | 303.30 | 231.82 | 136.45 | 38.72 |
| C0（c0-normal1） | 421.04 | 524.62 | 540.39 | 541.08 | 285.55 | 267.94 | 222.03 | 141.76 | 35.66 |
| A2（a-normal2） | 446.79 | 552.96 | 565.77 | 576.47 | 315.67 | 301.01 | 235.40 | 139.70 | 38.72 |
| D（d-normal1） | 430.50 | 534.58 | 551.32 | 557.65 | 312.81 | 300.04 | 222.90 | 131.61 | 35.88 |

第 3–12 轮各步骤从操作开始到目标页 IsLoaded 且数据就绪的中位数，单位为 ms。夹具每 15 ms 轮询，另记录 Low 优先级调度延迟和随后 120 ms 内的 Rendering 次数。转场可能仍在运行。

| 运行 | 收藏 | 专辑 | 艺术家 | 回歌曲 |
| --- | ---: | ---: | ---: | ---: |
| A1 | 135.86 | 78.82 | 40.53 | 107.48 |
| B1 | 123.65 | 68.14 | 36.38 | 101.97 |
| C0 | 111.44 | 62.31 | 36.16 | 95.78 |
| A2 | 129.77 | 75.16 | 38.31 | 100.40 |
| D | 111.95 | 62.42 | 35.43 | 93.34 |

末端 D 比两次 A 少 18.82–28.65 MiB Private Bytes、7.30–12.50 MiB 默认堆枚举忙块和 0.82–2.86 MiB 默认堆提交。托管堆提交也少 8.09–25.12 MiB。仅切换宿主的 B 末端默认堆枚举忙块和提交均处于两次 A 的近邻，单次 B 难以分离宿主变化的原生内存影响。D 的 Private Bytes 从第 1 轮到第 12 轮仍增长 120.82 MiB。最终版四个页签步骤的就绪中位数均低于两次 A。D 首轮与小库封面测试的收尾重叠，热轮独立运行，D 的 12 轮常规测量为单个进程样本。

同一最终 D 二进制另跑 24 轮（d-long24，PID 14020），导航断言全部通过。同一进程回到歌曲页时，第 1、8、12、16、20、24 轮的 Private Bytes 依次为 424.75、556.09、563.32、581.82、598.93、603.50 MiB。第 12 至 24 轮增加 40.18 MiB，最后 4 轮增加 4.57 MiB，尚未确认进入平台期。

诊断 GC 后再等 8 秒，Private Bytes 为 603.21 MiB。仅在该终点枚举默认堆，提交、已分配和忙块依次为 326.81、318.18、221.53 MiB。同期托管堆提交为 159.05 MiB，存活估计为 37.17 MiB。

### 原生堆追踪

A 的堆追踪先预热两轮，再覆盖两轮混合导航和末段等待。ETL 为 5,974,786,048 字节、53.06 秒，xperf 和两遍解析均记录 0 丢失。第 3 轮出生且第 3 轮歌曲页仍在途的默认堆分配为 69,207,516 B，到第 4 轮歌曲页剩 2,301,230 B，到诊断 GC 后 8 秒剩 1,996,090 B（2.88%）。同批 DirectWrite 从 38,030,725 B 降为 133,706 B，其中 99.65% 有匹配的 Free。末端较大的组包括 XAML 1,445,540 B、DirectWrite 133,706 B、coreclr 86,160 B、dwmcorei 85,084 B。收藏区间仍可见 19,200 B 的 SurfaceBrush 分配，持有者未确认。

B 的 ETL 为 6,067,060,736 字节、58.91 秒，采集和解析无丢失。相同口径的默认堆批次为 58,677,484 B，第 4 轮歌曲页剩 5,339,911 B，末端剩 4,066,514 B（6.93%）。同批 DirectWrite 从 37,656,618 B 降为 68,712 B，其中 99.82% 有匹配的 Free。末端 XAML 2,248,367 B 中可定位的组分配在布局 SizeChanged 和 RoutedEventArgs 的框架对象创建路径。dwmcorei 的 481,856 B 中有 403,424 B 解析到合成通道创建资源时的句柄表扩容。B 追踪与 D 编译重叠，耗时留作采集信息。

D 的 ETL 为 6,023,020,544 字节、49.32 秒，xperf 和两遍解析均无丢失。相同默认堆批次为 68,540,765 B，第 4 轮歌曲页剩 4,330,146 B，末端剩 2,589,774 B（3.78%）。DirectWrite 从 38,149,077 B 降为 57,768 B，其中 99.85% 有匹配的 Free。末端以 XAML 1,488,936 B、dwmcorei 402,512 B 和 marshal 383,096 B 为主。B 的合成通道句柄表扩容栈在 D 中仍有 329,856 B，收藏区间的 19,200 B SurfaceBrush 分配也仍在。D 的默认堆枚举忙字节低于两次 A，ETW 中这一批的末端在途字节高于 A 的同口径样本。采集范围涵盖默认堆，GPU 资源和采集前出生的块须另行测量。

## 37c5a649 续测与修正

**分配器范围。** 本机已安装播放器进程（PID 13216）用真实 MSIX 包身份启动，PEB 默认堆签名 ddeeddee 确认使用 Segment Heap。检查了嵌入 EXE 的清单，其中未显式设置 heapType。上文和本节的未打包旧诊断进程使用 NT Heap，其默认堆容量增长归于该运行环境。为对应已安装包的分配器，另用同型 Segment Heap 比较修前和修后的诊断发布。

在修前的同一诊断应用上，仅改变独立副本 EXE 的堆类型并使用相同实体 Unique 曲库。12 轮从 NT Heap 再到 Segment Heap，两个末轮 Private Bytes 为 521.59 与 458.81 MiB。另按反序各跑 8 轮，Segment Heap 与 NT Heap 末轮分别为 445.84 与 490.29 MiB。进程的实际堆签名均已核对，数据见 `artifacts/navigation-release-20261123/segment-heap-ab/summary.json`。

### 长跑与路线隔离

预设接近收敛的条件是取长跑末尾两个相邻且不重叠的 12 轮窗口。每个窗口在同一歌曲页采样的 Private Bytes 最小二乘斜率须 ≤0.5 MiB/轮，末 3 个样本的中位数减初 3 个样本的中位数须 ≤8 MiB，同时观察 GC 后存活量及默认堆忙块有无持续同步上涨。结果须在另一个进程复现。

96 轮混合轻量探针在第 24、48、72、96 轮回到歌曲页时的 Private Bytes 依次为 552.72、597.52、633.09、653.62 MiB。第 72 至 96 轮增加 20.53 MiB，默认堆提交增加 19.41 MiB（357.38 到 376.79 MiB），枚举忙块减少 1.88 MiB（227.46 到 225.58 MiB）。与托管堆提交量匹配的 Satori 私有区在这两个端点均为 158.14 MiB。就这两个端点而言，晚段变化集中在默认堆的已提交容量。轻量模式不在每个动作后枚举完整视觉树，仍观察到了增长。该次样本的曲库有 6,600 条记录，只有用于播放验证的 WAV 有实际文件，另外 6,599 条 FLAC 路径不存在。该次最后两窗（第 73–84 与第 85–96 轮）斜率分别为 1.00 和 0.72 MiB/轮，首尾三点中位数差为 9.02 和 6.50 MiB。原始记录见 `artifacts/navigation-release-20261123/library-switch-final-reload/publish-ab/isolated-run/d-long96-light`。

设置独立路线第 1、12、24 轮为 316.19、354.74、364.04 MiB，六个设置分区持续复用。旧夹具在终点固定期待八个页面，设置单路线只产生三个见证，因此这一轮只采用终点诊断前的轮末样本。修正诊断夹具后，专辑和艺术家路线 16 轮完整通过，Private Bytes 为 264.32、290.32、297.13 MiB（第 1、12、16 轮），歌曲页的行控件构造计数始终是 51。

独立实体基线（PID 28620，`artifacts/navigation-release-20261123/library-long-baseline-20261123/publish-physical/isolated-run/base72-physical`）完整通过 72 轮。第 24、48、72 轮 Private Bytes 为 543.88、595.96、636.49 MiB，诊断 GC 后再等 8 秒为 637.73 MiB。第 49–60 与第 61–72 轮的斜率分别为 1.24 和 1.91 MiB/轮，首尾三点中位数差为 10.08 和 17.82 MiB。第 48 至 72 轮默认堆提交由 346.00 增到 364.20 MiB，忙块由 222.17 增到 227.14 MiB。实体文件消除了缺文件异常的额外影响，NT Heap 的晚段增长仍可复现。

早期优化候选在相同实体曲库及 NT Heap 环境跑完 72 轮（PID 9212，`artifacts/navigation-release-20261123/library-long-candidate-20261123/publish-candidate/isolated-run/c0-physical72-light`）。第 24、48、72 轮 Private Bytes 为 474.96、511.96、550.11 MiB，GC 后再等 8 秒为 550.70 MiB。默认堆末端提交为 305.25 MiB，忙块为 189.57 MiB。末两窗斜率分别为 2.81 和 0.66 MiB/轮，首尾三点中位数差分别为 30.48 和 6.42 MiB，低水位和接近收敛条件在这里给出不同判断。

在同一裁剪诊断二进制和实体 Unique 数据上另跑两次各 16 轮。简化组从第 3 轮起省去夹具额外的歌单进入编辑态、选择和两次主动刷新，继续核对歌单详情列表、数量和首尾对应。简化组（PID 1208）与完整组（PID 29992）的第 16 轮 Private Bytes 为 460.91、461.88 MiB，后 8 轮斜率为 2.40、2.28 MiB/轮。在这次对照中两条路线的增长接近，原始记录在 `artifacts/navigation-release-20261123/continued-route-probe/publish-browse-witness/isolated-run` 下的 unique-browse16、unique-edits16。

### 歌曲列表屏外缓冲

歌曲、收藏、歌单详情和分组详情的歌曲列表保留 ItemsStackPanel，CacheLength 从默认 4 改为 2。WinUI 将这项设置以可视区长度分配到两侧，2 对应每侧一屏。 [ItemsStackPanel.CacheLength](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemsstackpanel.cachelength)

同一裁剪诊断二进制在创建窗口前只改 CacheLength，缺失文件的合成曲库各跑 16 轮。歌曲页实测容器数从 50 减至 30，全部歌曲列表累计行控件构造数从 161 减至 107，两组从第 2 轮结束起均稳定。诊断 GC 后再等 8 秒，Private Bytes 为 524.12 和 480.95 MiB，默认堆提交为 303.98 和 264.27 MiB，枚举忙块为 219.07 和 184.79 MiB。两组从第 1 到 16 轮的 Private Bytes 增量分别为 103.82 和 104.48 MiB，该对照观察到更低的内存高水位。

补做实际音频的短对照，使用与真实建库一致的 FLAC 和 WAV 扩展字段。2,199 首 FLAC 无图，4,400 首 FLAC 含封面，封面在四份样图间复用，另有 1 首 WAV。各跑 12 轮，歌曲页照样由 50 减至 30 个容器，累计歌曲行从 161 减至 107。CacheLength 4（PID 12192）与 2（PID 27180）末端 Private Bytes 为 527.91 和 464.95 MiB，默认堆提交为 298.64 和 253.09 MiB，默认堆忙块为 220.47 和 193.07 MiB。两臂内的专辑/艺术家卡片累计构造数、目录行视觉重置计数均在第 3 轮起保持稳定。样本目录位于 `artifacts/navigation-release-20261123/continued-route-probe/publish-canonical/isolated-run` 下面，名称为 canonical-cache4-12 和 canonical-cache2-12，两边均有 measurements.csv、constructions.csv 和末端堆快照。

独立 WinUI 2.7 夹具用原生列表重复交错测试 4、2 和 1。CacheLength 4 与 2 时顶部/中段容器数分别为 32/32 和 19/21。全部 90 次快速跳滚和 503 次布局采样可见区均完整，定位误差约 0.01 DIP，返回滚动锚点误差为 0 DIP，页签焦点保持。使用 2 的定位时长与 4 相近，1 的一个样本更慢。

### 专辑与艺术家面板的诊断对照

只在诊断副本缩小两个分组 GridView 的屏外缓冲。原生面板为水平 ItemsWrapGrid。两个版本的专辑与艺术家各有 330 张卡，保持原分组、面板方向和项目模板，歌曲缓冲固定为 2。实体 Unique 数据的专辑与艺术家独立路线各跑 10 轮，默认 4（PID 21512）与候选 2（PID 11092）分别保留每页 56 与 36 个容器，两边的卡片构造数都在预热后保持稳定。诊断 GC 后再等 8 秒，Private Bytes 为 252.21 与 242.88 MiB，默认堆提交为 103.45 与 93.82 MiB，忙块为 74.96 与 69.23 MiB。第 4 至 10 轮累计托管分配斜率由 4.67 降至 3.25 MiB/轮，Private Bytes 斜率两组均约为 1.15 MiB/轮。结果来自 NT Heap，支持这条短路线的开销下降。原始数据在 `artifacts/navigation-release-20261123/continued-route-probe/publish-group-panels/isolated-run` 的 group-cache4-10 和 group-cache2-10。

另用 26 个字母分组检查实际交互。两组的专辑和艺术家各完成 5 次快速跳转，每次均看到目标卡片。详情返回后的锚点均为 9242.67 DIP。直接切换原生语义缩放后，两组均回到 0 DIP，随后可重新定位到目标。输出见 `continued-route-probe/publish-group-witness/isolated-run` 的 group-letters4、group-letters2 启动日志。

### 缺失音频的封面回退

旧缺文件夹具在第 12 轮结束后采集了 25 秒（PID 30896）。380 个带堆栈的 GCAllocationTick 共加权 53,733,192 B，封面路径占 71.7%，其中 ATL 嵌图解析 21,361,856 B，异常格式化 11,630,296 B。原始跟踪位于 `artifacts/navigation-release-20261123/library-long-baseline-20261123/publish-long/isolated-run/base72-checked/gc-short.nettrace`，归类见 `artifacts/library-allocation-attribution/baseline.json`。这些数字来自托管分配采样。

缺失本地音频现在跳过嵌图解析，目录封面与远程回退仍可工作。`_tools/SmtcCoverRegression` 的 12 项验证通过，覆盖缺文件时零嵌图调用、目录图、恢复文件及保留有效已有结果。实体长跑仍按独立数据评估。

### 同安装版堆类型的长跑

独立诊断发布使用与已安装版相同的 Segment Heap。修前基线（PID 28440）以 Unique 实体曲库跑完 96 轮。第 24、48、72、96 轮同歌曲页的 Private Bytes 分别为 473.77、493.80、510.17 和 518.03 MiB，诊断 GC 后再等 8 秒为 513.97 MiB。第 73–84 轮和第 85–96 轮的斜率为 0.242 和 0.234 MiB/轮，三点中位数差为 3.45 和 2.73 MiB。该进程的 Private Bytes 已满足两窗门槛。原始数据见 `artifacts/navigation-release-20261123/segment-heap-ab/publish-segment/isolated-run/segment-base96`，堆类型见 heap-backend.txt。

最终源码候选 C1 在两个独立进程跑相同的 96 轮。歌曲页始终为 30 个容器。数据在 `artifacts/navigation-release-20261123/l1/segment/isolated-run` 下的 c1-segment96-a2、c1-segment96-b2，实际堆类型记录在各自 heap-backend.json。

| C1 进程 | 第 24 轮 | 第 48 轮 | 第 72 轮 | 第 96 轮 | GC 后再等 8 秒 | 第 73–84 轮斜率/中位数差 | 第 85–96 轮斜率/中位数差 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| PID 9616 | 425.37 | 439.66 | 453.40 | 469.42 | 470.19 | 1.306 / 12.39 | 0.108 / 1.80 |
| PID 28448 | 425.60 | 440.16 | 456.05 | 467.28 | 455.92 | 0.307 / 1.76 | 0.166 / 2.50 |

内存与中位数差为 MiB，斜率为 MiB/轮。两次的第 96 轮均比同堆类型的修前基线低约 49–51 MiB。第 72 至 96 轮默认堆忙块分别为 183.21 到 183.95 MiB、181.42 到 181.10 MiB。GC 后的托管存活估计为 32.91 和 32.28 MiB。PID 28448 通过两窗门槛，PID 9616 通过最后一窗，96 轮的接近收敛结果尚未独立复现。

全新目录的 Release x64 裁剪 MSIX 已解包验证，确认歌曲列表面板、封面回退和歌词渐变代码均保留。正式包位于 `artifacts/navigation-release-20261123/library-long-official-20261123/msix/WinUIMusicPlayer_1.3.3.0_x64_Test/WinUIMusicPlayer_1.3.3.0_x64.msix`，SHA-256 为 `DB94221B36EC7A9637E303A12ABF62BD04F89FB060887D600B59D76158C3F3A0`。正式 EXE 清单沿用 MSIX 的堆选择。

### 从本轮隔离产物复跑

工作区中的 `artifacts/navigation-release-20261123/library-long-baseline-20261123` 保存基线来源副本、`Run.ps1`、已裁剪的 `publish-physical` 和 `fixture`。实体长跑使用 2,199 首无图 FLAC、4,400 首带图 FLAC 和 1 首 WAV。220 个有图专辑各有独立封面，同一专辑的 20 首共享该图。实体音频由 `Install-PhysicalFixture.ps1` 放入本次独立目录，扩展字段使用正常建库的 FLAC 和 WAV 写法。长跑脚本的采样包含 `measurements.csv`、`phase-telemetry.csv`、每阶段的 `*-heaps.csv` 和 `*-vm.csv`。

在仓库根目录对基线产物执行以下命令，使用从未运行过的名称。脚本启动后台私有桌面，曲库、日志、用户目录重定向与缓存均指向这次运行的 `isolated-run` 子目录。超过 1,536 MiB 时脚本结束自己的进程，并保留已采样数据。

```powershell
$probe = Join-Path $PWD 'artifacts/navigation-release-20261123/library-long-baseline-20261123'
& "$probe/Run.ps1" -Publication publish-physical -Name verify-unique72 -Rows 6600 -Cycles 72 -Route full -NoAudio -Light -Telemetry -FixtureMode Unique -PrivateLimitMiB 1536
```

## 从干净检出复跑

在独立 Windows 账户或虚拟机运行。使用固定的 6,600 首无个人内容的测试曲库，全程不点击播放。准备一个独立目录和一份含 500 首的歌单，保存测试曲库以便两个进程从相同初始数据启动。记录 Windows 版本、.NET SDK、两份 WinUI DLL 的版本和 SHA-256、窗口尺寸、系统动画状态、数据分组数及采集配置。

在源码根目录用项目要求的 .NET 11 SDK 发布到空目录。以下命令生成 Release x64 非打包发布，其默认项目配置启用裁剪。

```powershell
dotnet --info
dotnet publish ./WinUIMusicPlayer.csproj -c Release -r win-x64 `
    -p:Platform=x64 -p:WindowsPackageType=None -p:GenerateAppxPackageOnBuild=false `
    -p:AppxSymbolPackageEnabled=false -o ./artifacts/native-memory-repro/publish
```

如需从零准备手工复跑数据，可使用 FFmpeg 生成带元数据的静音 FLAC。执行时曲库尚未添加该路径。生成内容保存在当前 checkout 的单独测试目录。

```powershell
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$music = Join-Path (Get-Location) 'artifacts/native-memory-repro/music'
if (Test-Path -LiteralPath $music) { throw '测试曲库目录已存在' }
$null = New-Item -Path $music -ItemType Directory
foreach ($album in 0..329) {
    $collection = [int][Math]::Floor($album / 100)
    $folder = if ($album -eq 0) { $music } else {
        Join-Path $music ('Collection{0:D4}/Edition{1:D5}' -f $collection, $album)
    }
    $null = New-Item -Path $folder -ItemType Directory -Force
    $ffArgs = @('-hide_banner','-nostdin','-loglevel','error','-n','-f','lavfi','-i','anullsrc=r=44100:cl=stereo')
    foreach ($track in 0..19) {
        $index = $album * 20 + $track
        $title = 'Track {0:D7}' -f $index
        $artist = 'Artist {0:D4}' -f (($album * 37) % 2400)
        $albumName = 'Album {0:D5}' -f $album
        $path = Join-Path $folder ('Track{0:D7}.flac' -f $index)
        $ffArgs += @('-map','0:a','-t','0.2','-c:a','flac','-threads','1',
            '-metadata',"title=$title",'-metadata',"artist=$artist",'-metadata',"album=$albumName",
            '-metadata',"track=$($track + 1)",$path)
    }
    & $ffmpeg @ffArgs
    if ($LASTEXITCODE -ne 0) { throw "第 $album 张测试专辑生成失败" }
}
if (@(Get-ChildItem -LiteralPath $music -Recurse -File -Filter '*.flac').Count -ne 6600) {
    throw '测试曲库数量不符'
}
```

导入目录后确认总数，创建 500 首歌单并保存初始数据。若要复现收藏页负载，将测试库每十首中的首首设为收藏，或记录手工版本的实际收藏数。手工静音文件与诊断夹具直接写测试库的方式不同。

自动化夹具中的固定分布为 330 张专辑，每张 20 首，按 `(专辑序号 × 37) % 2400` 生成 330 位不同歌手，每 10 首标记一个收藏。首张专辑的曲目直接放在根目录，其余按每 100 张专辑一个 Collection 子目录、每张专辑一个 Edition 目录分组。手动导入无个人内容的测试曲目也可复现近似负载，报告时注明实际专辑、歌手、收藏和文件夹曲目数。

一轮从曲库开始，按歌曲、收藏、专辑、歌手、目录、500 首歌单、设置顺序操作。设置进入后等常规卡片可见，展开再折叠可见项，切到 DSP、歌词，展开再折叠歌词设置项，继续到快捷键、关于，回常规并展开一项；偶数轮可进封面背景。最后回歌曲页，等真实列表出现再记样。每次记录曲库 6,600 首、500 首歌单已进入详情、目录路径和列表一致、设置重进仍在常规。对比相同位置的第 1、8、12 轮，并在末轮原页等待至少 4 秒、再等 8 秒。自动化限速与诊断 GC 时点须单独记录，人工复跑的暂停不能替代夹具的诊断 GC 样本。

同时在第二个 PowerShell 采集窗口用目标 PID 读取每个同页测量点的 Private Bytes 和 Working Set。先在第二个窗口设置同一目标 PID，每到测量点执行下方最后两行。保存 QPC 和 UTC，分别用于原始堆事件与普通日志的对齐。不要跨进程比较相同指针地址。

```powershell
$target = Get-Process -Id ([int](Read-Host '隔离测试实例 PID'))
$target.Refresh()
[pscustomobject]@{ At = [DateTime]::UtcNow.ToString('o'); Qpc = [Diagnostics.Stopwatch]::GetTimestamp(); PrivateBytes = $target.PrivateMemorySize64; WorkingSet = $target.WorkingSet64 }
```

本轮的进程堆采集使用 Windows Performance Toolkit 的 xperf。启动自己的测试实例，确认 PID 和可执行文件路径对应本次发布。下面只开启和关闭本次唯一命名的采集会话，需具备相应 ETW 权限。采集开始后才计入分配事件，适合比较共同预热之后出现的增量。本次使用顺序文件，6 GiB 限额下可完整记录约两轮。人工路径用两轮窗口和足够磁盘空间；先做一轮预热。[xperf 现有进程采集](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/enabling-heap-data-capture)

```powershell
$xperf = (Get-Command xperf.exe -ErrorAction Stop).Source
$target = Get-Process -Id ([int](Read-Host '隔离测试实例 PID'))
$target | Select-Object Id, Path
$etl = Join-Path (Get-Location) ("native-heap-{0}.etl" -f [Guid]::NewGuid().ToString('N'))
$session = "OriginalSoundHeap-" + [Guid]::NewGuid().ToString('N')
& $xperf -start $session -heap -Pids $target.Id -StackWalk HeapAlloc+HeapRealloc `
    -BufferSize 1024 -MinBuffers 160 -MaxBuffers 256 -MaxFile 6144 -FileMode Sequential `
    -ClockType PerfCounter -f $etl
if ($LASTEXITCODE -ne 0) { throw '堆 ETW 会话启动失败' }
try {
    Read-Host '完成导航和末段测量后，保持测试进程打开并按 Enter'
} finally {
    & $xperf -stop $session
    if ($LASTEXITCODE -ne 0) { throw "检查自有会话是否关闭 $session" }
}
& $xperf -i $etl -a tracestats
```

在 WPA 中按 PID、Heap Handle 和 Allocation Stack 分组，用两个截止时点的在途字节比较同一批路由产生的保留量。确认采集没有事件丢失或缓冲区覆盖，记录未匹配释放及无栈字节。应用和系统模块须用匹配的 PDB，未匹配时保留 DLL 和 RVA。累计分配总量反映负载，端点仍存活的分配才用于这一归因。[xperf 的在途分配口径](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/xperf/using-actions-to-analyze-process-heap-data)

### 原始 QPC 逐块复核

[HeapCohorts.cpp](../_tools/NativeHeapCohorts/HeapCohorts.cpp) 是离线只读分析器。已在上述 Windows x64 验证 xperf 私有 heap provider 的 64 位 Alloc/Realloc/Free/Destroy 格式及 QPC 的 StackWalk 格式。工具遇到 ETW 丢失、未知事件形状、找不到目标堆事件或无法关联任何阶段调用栈时返回错误。使用 Visual Studio 的 x64 Developer PowerShell 编译。

```powershell
$analysis = Join-Path (Get-Location) 'artifacts/native-memory-repro'
$null = New-Item -Path $analysis -ItemType Directory -Force
cl /nologo /std:c++17 /W4 /WX /EHsc /O2 `
    ./_tools/NativeHeapCohorts/HeapCohorts.cpp advapi32.lib `
    "/Fo$analysis/HeapCohorts.obj" "/Fe$analysis/HeapCohorts.exe"
if ($LASTEXITCODE -ne 0) { throw '分析器构建失败' }
```

用终端测量时保存每个标记的 `Qpc` 值。保存目标 PID 的默认堆地址，其值可由目标自己调用 `GetProcessHeap()` 取得。也可在正式采集前用 Windows SDK 的 cdb 非侵入式读取 PEB 的单个 ProcessHeap 字段。此操作可能短暂停顿。以下命令将符号缓存写在实验目录中。[Microsoft 非侵入式附加说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/noninvasive-debugging--user-mode-)

```powershell
$target = Get-Process -Id ([int](Read-Host '隔离测试实例 PID'))
$cdb = (Get-Command cdb.exe -ErrorAction Stop).Source
$symbols = Join-Path $analysis 'symbols'
$heapLine = & $cdb -pv -p $target.Id -y "srv*$symbols*https://msdl.microsoft.com/download/symbols" `
    -c 'dt ntdll!_PEB @$peb ProcessHeap; q' | Select-String 'ProcessHeap\s*:' | Select-Object -First 1
if (!$heapLine) { throw '未读取到默认堆地址' }
$heapText = $heapLine.Line.Replace([string][char]0x60, '')
if ($heapText -notmatch '0x([0-9A-Fa-f]+)') { throw '未读取到默认堆地址' }
$heap = $Matches[1]
```

保存同一 PID 的模块加载地址。下例生成 `modules.csv`，可在实验开始和结束分别执行并保留两份结果。早期模块已经卸载时，仅在后续未复用该模块地址的情况下合并。若公共 PDB 不匹配，保留模块和 RVA。

```powershell
$target.Refresh()
$target.Modules | ForEach-Object {
    [pscustomobject]@{
        base = $_.BaseAddress.ToInt64().ToString('X')
        size = $_.ModuleMemorySize
        name = $_.ModuleName
        path = $_.FileName
    }
} | Export-Csv -LiteralPath (Join-Path $analysis 'modules.csv') -NoTypeInformation
```

关闭自有 xperf 会话后分析，填入本次实际 QPC 与 ETL 路径。普通模式的 `PHASE` 依次是标记、QPC、块数、字节、带栈块数、带栈字节、歧义块数和歧义字节。`--cohort all 起点QPC 终点QPC` 跟踪指定出生区间且在该区间终点仍在途的块。`COHORT_TOP` 按当前同出生区间、相同大小和相同分配栈的在途字节显示，`COHORT_STACK` 对比出生终点的固定前列栈。`COHORT_RETIRED` 的 op36 为匹配到 Free。以模块名替代 `all` 可只追踪其最靠近分配器的栈。

```powershell
$etl = Read-Host '本次已关闭的 ETL 路径'
$round2 = [long](Read-Host '第 2 轮歌曲页终点 QPC')
$round3 = [long](Read-Host '第 3 轮歌曲页终点 QPC')
$round4 = [long](Read-Host '第 4 轮歌曲页终点 QPC')
$idle = [long](Read-Host '末段静置终点 QPC')
$marks = @("round-2=$round2", "round-3=$round3", "round-4=$round4", "idle=$idle")
& "$analysis/HeapCohorts.exe" $etl $target.Id $heap "$analysis/modules.csv" @marks `
    > "$analysis/default-heap.tsv"
if ($LASTEXITCODE -ne 0) { throw '阶段分析失败' }
& "$analysis/HeapCohorts.exe" $etl $target.Id $heap "$analysis/modules.csv" @marks `
    --cohort all $round2 $round3 > "$analysis/round-3-cohort.tsv"
if ($LASTEXITCODE -ne 0) { throw '分配批次分析失败' }
```

可用独立测试进程验证阶段解析。先用 `HeapAlloc` 分配 229,461 字节，暂停并记 QPC，再以 `HeapReAlloc` 增加到 321,197 字节后记 QPC，最后 `HeapFree` 并记 QPC。核验 `SIZE` 中两尺寸依次出现、释放后均不在途，`MATCH` 能区分移动与原地 realloc。
