---
title: "Periodic induced full GC (InducedNotForced) every ~7.5s from native layer causes 30-45ms STW hitches in WinRT-heavy WinUI 3 apps"
labels: ["area-Performance", "bug"]
---

## Summary

A WinUI 3 app with a WinRT-heavy UI (Win2D + Composition + ComputeSharp) experiences periodic **30-45ms STW hitches every ~7.5 seconds** while rendering (shader background + animated lyrics). The hitches are caused by a **`GC.Collect(blocking:false)` (GCReason=InducedNotForced) triggered from the native layer** every ~7.5s once the app is active. A freshly launched idle app produces **zero GCs in 20 seconds**; the periodic GC only appears after the user interacts (navigates pages / starts playback).

Reproducing app: https://github.com/Johnwikix/original-sound-hq-player

## Environment

- WindowsAppSDK 2.3.1 (self-contained), WinUI 3
- .NET 11 (net11.0-windows10.0.26100.0), CsWinRT Runtime 2.2.0
- Microsoft.Graphics.Win2D 1.4.0, ComputeSharp.D2D1.WinUI
- Repro time: 30-45ms per hitch, ~7.5s period, time-in-gc only ~0.4-0.7%

## Evidence

### 1. Periodic induced full GCs (dotnet-trace, GC keyword)

```
GCStart timeline (54s window):
  t+0.0s   Gen2 34.7ms  InducedNotForced
  t+9.0s   Gen2 35.9ms  InducedNotForced
  t+16.1s  Gen2 32.6ms  InducedNotForced
  t+23.4s  Gen2 45.2ms  InducedNotForced
  t+31.5s  Gen2 42.4ms  InducedNotForced
  t+40.3s  Gen2 35.7ms  InducedNotForced
  t+47.2s  Gen2 35.2ms  InducedNotForced
  t+54.3s  Gen2 34.0ms  InducedNotForced
```

Interval is almost exactly ~7.5s. All GCs start on a threadpool-like thread.

### 2. The pause is NOT heap-size driven

GCHeapStats during those GCs: managed heap is only **~42MB total** (Gen0 ~5MB, Gen1 ~2.4MB, Gen2 ~28MB, LOH ~8MB), yet each full GC takes 30-45ms. The cost is dominated by:

| Metric | Value |
|---|---|
| **GCHandleCount** | **~200,000-240,000 (steady)** — WinRT RCW tracking handles, walked on every GC |
| **FinalizationPromotedCount per GC** | **15,000-28,000** and *growing with session activity* (~20k while browsing, ~28k while rendering) |

### 3. The GC.Collect caller is NOT managed code

- Scanned **every managed DLL in the app folder** at the MemberRef level (System.Reflection.Metadata): the only `System.GC::Collect` callers are the app's own assembly (2 call sites in a working-set-trim helper). WinRT.Runtime / Microsoft.Windows.SDK.NET only call `AddMemoryPressure`/`RemoveMemoryPressure`.
- Instrumented that helper with a caller-stack logger and rebuilt: **zero invocations** during idle and rendering windows while the periodic GCs continued.
- Conclusion: the periodic `GC.Collect(blocking:false)` originates from the **native layer** (XAML runtime / WindowsAppRuntime component). It cannot be disabled from the app.

### 4. Fresh idle app: zero GCs

A freshly launched, untouched app produced **no GC events at all in 20 seconds**. The 7.5s periodic pattern activates only after app activity (page navigation / playback / rendering starts).

## Related: CsWinRT GC pressure amplification (JIT vs AOT)

The same app is almost hitch-free in **NativeAOT with CsWinRT Runtime 2.2.0**, and returns to stuttering in AOT after upgrading to **CsWinRT 2.3.1** (microsoft/CsWinRT#1933 removed the `RuntimeFeature.IsDynamicCodeCompiled` guard, re-enabling `GC.AddMemoryPressure(100KB)` per object reference on AOT).

CsWinRT adds a fixed 100KB phantom memory pressure per object reference (`GC_PRESSURE_BASE`, hard-coded in `ObjectReference`). In this RCW-heavy app the live wrapper population is in the hundreds of thousands (see `GCHandleCount` above), so the cumulative phantom pressure scales to many times the size of the real managed heap (~42MB). This is arguably a CsWinRT scalability concern (a fixed per-reference cost is crude for RCW-heavy apps), but the **root anomaly is the native periodic full GC** described above.

## Questions

1. Which WindowsAppSDK native component triggers `GC.Collect(blocking:false)` every ~7.5s once the app is active? Is it the XAML runtime's periodic sweep? Can it be disabled or is its period configurable?
2. For WinRT-heavy apps holding 200k+ RCWs, is the per-GC GCHandle walk + finalizer batch cost a known concern? Is there a recommended pattern to keep the tracked-RCW footprint bounded?
3. Why does the sweep only start after app activity (fresh idle = zero GCs)?

## Repro steps

1. Any WinUI 3 app with a Composition/Win2D-heavy UI (e.g., animated canvas rendering at 60fps, many WinRT objects alive).
2. Launch, navigate a few pages / start playback, keep the UI active.
3. `dotnet-trace collect -p <pid> --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:4" --duration 00:00:35`
4. Observe: `GCStart` with Reason=`InducedNotForced` every ~7.5s, 30-45ms pauses, `GCHeapStats.GCHandleCount` in the 200k+ range while the managed heap is < 50MB.

## Workarounds tried (app side)

- `<ServerGarbageCollection>true</ServerGarbageCollection>`: pauses 30-45ms → 26-40ms (~30-40% shorter), pattern unchanged. Note: on .NET 11 this runs the server GC with **DATAS** (Dynamic Adaptation to Application Sizes, the default server-GC mode since .NET 9), so the measurements above reflect the DATAS mode.
- Removed app-side induced `GC.Collect(blocking:false)` calls: no change to the native pattern.

---

*This issue report was generated with the assistance of an AI assistant.*
