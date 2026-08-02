# WinUI 3 播放详情页渲染卡顿：GC 诊断报告

> 日期：2026-08-02
> 项目：https://github.com/Johnwikix/original-sound-hq-player
> 状态：已定位根因，待平台侧回应（issue 已备好，见 `doc/issue-windowsappsdk-periodic-gc.md`）

## 1. 症状

- **非 AOT（JIT）构建**下，播放详情页（PlayingDetail）的 shader 背景和高级逐字歌词渲染出现周期性卡顿；点击过的页面越多越明显。
- **AOT（NativeAOT）+ WindowsAppSDK 自带 CsWinRT 2.2.0**：相同操作几乎无卡顿。
- **AOT + 手动升级 CsWinRT 2.3.1**：卡顿复现，与 JIT 一致。

## 2. 环境

| 项 | 值 |
|---|---|
| 应用 | OriginalSound HIFI Player（WinUI 3 音乐播放器） |
| TargetFramework | net11.0-windows10.0.26100.0 |
| WindowsAppSDK | 2.3.1（SelfContained） |
| CsWinRT Runtime | 2.2.0（WindowsAppSDK 内置） |
| Win2D | Microsoft.Graphics.Win2D 1.4.0（CsWinRT 投影） |
| ComputeSharp | D2D1 WinUI（shader 背景） |
| GC | 工作站在线 GC（后启用 ServerGC 对比） |

## 3. 诊断方法与数据（5 次 dotnet-trace，GC 事件 + 堆统计）

### 3.1 核心发现：周期性诱导全量 GC

- **每 ~7-11 秒一次 `GC.Collect(blocking:false)`**（GCReason = `InducedNotForced`）——非固定周期，跨会话实测间隔 6.7~11.6s（早期会话均值 ~7.5-7.8s，随时间略拉长）。例如 0 / 8.8 / 15.5 / 22.7 / 30.6s。
- 每次 **Gen2 STW 30~45ms**（ServerGC 后 26~40ms），总 time-in-gc 仅 ~0.4-0.7%，但单次停顿足以掉帧 2-3 次。
- **全新启动、不操作的应用：20 秒内零 GC 事件**——周期 GC 在用户操作（浏览页面/播放）之后才激活。

### 3.2 为什么 42MB 的堆要扫 30-45ms

GCHeapStats 显示托管堆仅 ~42MB，但：

| 指标 | 值 | 说明 |
|---|---|---|
| GCHandle 数 | **~200,000-240,000（恒定）** | WinRT RCW 追踪句柄；每次 GC 全量遍历 |
| 每周期新增终结对象 | **15,000-28,000** | 浏览期 ~20k，渲染期 ~28k（随时间增长） |
| 托管堆 | Gen0~5MB + Gen1~2.4MB + Gen2~28MB + LOH~8MB | 停顿与堆大小无关 |

停顿成本 = GCHandle 遍历 + 终结器批处理，而非堆扫描。

### 3.3 GC.Collect 调用者排查

- **全 AppX 目录托管 dll 元数据扫描**（System.Reflection.Metadata 解析 MemberRef）：只有应用自身 dll 调用 `System.GC::Collect`（WorkingSetCompressor 两处），WinRT.Runtime / Microsoft.Windows.SDK.NET 仅调用 Add/RemoveMemoryPressure。
- **应用插桩**（WorkingSetCompressor 加调用栈日志 + Release 重建）：空闲 30 秒 + 渲染期间**零次调用**。
- **推测：周期 GC.Collect 可能来自原生层**（Microsoft.ui.xaml.dll / WindowsAppRuntime 等 WindowsAppSDK 组件）。但此结论基于"未找到托管调用者"的排除法，不排除应用自身存在未识别的触发因素——issue 中已按此口径表述。

### 3.4 JIT / AOT 差异与 CsWinRT PR #1933

| 配置 | CsWinRT 内存压力 | 结果 |
|---|---|---|
| JIT + 2.2.0 | 开（`if (RuntimeFeature.IsDynamicCodeCompiled)` 为 true） | 卡 |
| AOT + 2.2.0 | 关（AOT 下 IsDynamicCodeCompiled = false） | 流畅 |
| AOT + 2.3.1 | **PR #1933 删除守卫，恢复开启** | 卡复现 |

机制：每个 RCW 包装器构造时 `GC.AddMemoryPressure(100KB)`（`GC_PRESSURE_BASE`，CsWinRT `ObjectReference` 硬编码）。本应用活包装器数以十万计（见 3.2 节 GCHandleCount），累计幽灵压力远超真实托管堆（~42MB）——固定每引用成本对 RCW 重度应用缺乏可扩展性。2.3.1 的 AOT 回归与 PR #1933（"Always enable GC pressure"，原守卫注释引用的 dotnet/runtime#104583 已修复）完全吻合。

### 3.5 CsWinRT 3.0-preview 检查

`WindowsRuntime.InteropServices.WindowsRuntimeObjectReference.GCPressureBaseInBytes` 为 **private static literal**——压力基数不可配置，无关闭开关。

## 4. 根因链

```
WinRT 重度 UI（~24 万活 RCW） + 每 ~7-11s 疑似原生层强制 GC.Collect
    → 每次全量 GC 遍历 24 万 GCHandle + 1.5-2.8 万终结器
    → 30-45ms STW（JIT 下再被 CsWinRT 每引用 100KB 固定压力放大）
    → 播放详情渲染（Win2D + ComputeSharp 每帧创建/释放 RCW）感知为卡顿
    （注：根因归属平台或应用尚未定论，详见 3.3 与 issue）
```

## 5. 已应用的缓解（非视觉改动）

1. `<ServerGarbageCollection>true</ServerGarbageCollection>`：全量 GC 并行化，停顿 -30~40%。注：.NET 11 上 ServerGC 实际运行 **DATAS**（Dynamic Adaptation to Application Sizes，.NET 9 起默认的 Server GC 自适应模式），本节数据均为 DATAS 模式下的测量。
2. 删除应用内三处诱导性 `GC.Collect(GC.MaxGeneration, Optimized, blocking:false)`（导航/进出播放详情时的手动全量 GC）。
3. CsWinRT 保持 2.2.0（AOT 无压力放大）。

## 6. 待平台侧回答的问题

1. 是否有 WindowsAppSDK/WinUI 组件在应用活动后周期性（~7-11s）调用 `GC.Collect(blocking:false)`？是否为已知行为、可否配置？
2. 20 万+ GCHandle（RCW 追踪）的基数本身是否就是根因？WinUI 3 重度 UI 应用的合理范围与收敛建议？
3. 针对此类应用的 GCHandle 遍历 + 终结器批处理成本，是否有推荐优化方式？

## 7. 复现要点

- 任意 WinRT 重度 WinUI 3 应用（大量 Composition/Win2D 对象）。
- 启动后浏览几个页面/播放内容，保持 UI 活动。
- `dotnet-trace collect -p <pid> --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:4" --duration 00:00:35`
- 观察：GCReason=InducedNotForced 每 ~7-11s 一次；GCHeapStats GCHandleCount ~20 万+。
