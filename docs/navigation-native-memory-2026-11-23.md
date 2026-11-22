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
