---
title: "Looking for guidance: recurring induced full GC (InducedNotForced) every ~7-11s causing 30-45ms STW hitches in a WinRT-heavy WinUI 3 app"
labels: ["area-Performance"]
---

## Summary

We're a WinUI 3 music player app with a WinRT-heavy UI (Win2D + Composition + ComputeSharp). While rendering the "now playing" screen (shader background + animated lyrics), we see **30-45ms STW hitches recurring roughly every 7-11 seconds**, which look like periodic `GC.Collect(blocking:false)` calls (`GCReason=InducedNotForced`). A freshly launched, untouched app produces **zero GCs in 20 seconds**; the pattern only appears after the app is active (navigating pages / starting playback).

**We're not sure whether this is a platform behavior or something our app is doing wrong.** We did our best to investigate, and we'd really appreciate any guidance — including "this is expected / this is your app's fault" — from people who know the platform internals better than we do.

Reproducing app: https://github.com/Johnwikix/original-sound-hq-player

## Environment

- WindowsAppSDK 2.3.1 (self-contained), WinUI 3
- .NET 11 (net11.0-windows10.0.26100.0), CsWinRT Runtime 2.2.0
- Microsoft.Graphics.Win2D 1.4.0, ComputeSharp.D2D1.WinUI
- Repro time: 30-45ms per hitch, recurrence every ~7-11s (measured intervals 6.7-11.6s across sessions), time-in-gc only ~0.4-0.7%

## What we observed

### 1. Recurring induced full GCs (dotnet-trace, GC keyword)

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

Intervals are roughly 7-11s, not a fixed period. Measured intervals across sessions: 8.8, 6.7, 7.2, 7.9 / 9.0, 7.1, 7.3, 8.1, 8.8, 6.9, 7.1 / 9.1, 9.3, 10.5, 11.6s (avg ~7.5-7.8s in early sessions, drifting longer over time). All GCs start on a threadpool-like thread.

### 2. The pause does not seem to be heap-size driven

GCHeapStats during those GCs: managed heap is only **~42MB total** (Gen0 ~5MB, Gen1 ~2.4MB, Gen2 ~28MB, LOH ~8MB), yet each full GC takes 30-45ms. The cost seems dominated by:

| Metric | Value |
|---|---|
| **GCHandleCount** | **~200,000-240,000 (steady)** — WinRT RCW tracking handles, walked on every GC |
| **FinalizationPromotedCount per GC** | **15,000-28,000** and *growing with session activity* (~20k while browsing, ~28k while rendering) |

We fully accept that the very large RCW footprint here (200k+ GCHandles) might be an app-side problem — our UI is unusually WinRT-heavy by design (per-frame Win2D/ComputeSharp rendering, lots of Composition objects). If the size of this footprint is the real issue, we'd love to know the expected/best-practice range for a WinUI 3 app.

### 3. We could not find a managed caller for the GC.Collect

- Scanned every managed DLL in the app folder at the MemberRef level (System.Reflection.Metadata): the only `System.GC::Collect` callers are the app's own assembly (2 call sites in a working-set-trim helper). WinRT.Runtime / Microsoft.Windows.SDK.NET only call `AddMemoryPressure`/`RemoveMemoryPressure`.
- Instrumented that helper with a caller-stack logger and rebuilt: zero invocations during idle and rendering windows while the recurring GCs continued.

Based on this we *suspect* the trigger lives outside our managed code, but we may well be missing something — e.g., a way our app's usage pattern causes some runtime/component to induce these GCs. Please correct us if so.

### 4. Fresh idle app: zero GCs

A freshly launched, untouched app produced no GC events at all in 20 seconds. The recurring GC pattern activates only after app activity (page navigation / playback / rendering starts).

## Related observation: JIT vs AOT difference (CsWinRT GC pressure)

The same app is almost hitch-free in **NativeAOT with CsWinRT Runtime 2.2.0**, and returns to stuttering in AOT after upgrading to **CsWinRT 2.3.1** (microsoft/CsWinRT#1933 removed the `RuntimeFeature.IsDynamicCodeCompiled` guard, re-enabling `GC.AddMemoryPressure(100KB)` per object reference on AOT). This correlation is what led us to suspect the pressure mechanism plays a role, but we're not certain it's the whole story.

CsWinRT adds a fixed 100KB phantom memory pressure per object reference (`GC_PRESSURE_BASE`, hard-coded in `ObjectReference`). With our large wrapper population (hundreds of thousands, see `GCHandleCount` above), the cumulative phantom pressure scales to many times the size of the real managed heap (~42MB). We're not claiming this is a bug — it might be a deliberate trade-off that our app happens to stress, and if so we'd appreciate any recommended mitigations.

## Questions

1. Is a recurring `GC.Collect(blocking:false)` (InducedNotForced) every ~7-11s, activating only after app activity, a known behavior of any WindowsAppSDK/WinUI 3 component (e.g., the XAML runtime)? If so, is it configurable or documented anywhere?
2. Is our RCW/GCHandle footprint (~200-240k handles) itself the likely root cause? What's the expected range for a UI-heavy WinUI 3 app, and is there a recommended pattern to keep it bounded?
3. Any guidance on reducing the per-GC cost of the GCHandle walk + finalizer batches for such apps?

Any pointers — even "this is expected, here's why" — would be very much appreciated.

## Repro steps

1. Any WinUI 3 app with a Composition/Win2D-heavy UI (e.g., animated canvas rendering at 60fps, many WinRT objects alive).
2. Launch, navigate a few pages / start playback, keep the UI active.
3. `dotnet-trace collect -p <pid> --providers "Microsoft-Windows-DotNETRuntime:0x4C14FCCBD:4" --duration 00:00:35`
4. Observe: `GCStart` with Reason=`InducedNotForced` recurring every ~7-11s, 30-45ms pauses, `GCHeapStats.GCHandleCount` in the 200k+ range while the managed heap is < 50MB.

## What we tried (app side)

- `<ServerGarbageCollection>true</ServerGarbageCollection>`: pauses 30-45ms → 26-40ms (~30-40% shorter), pattern unchanged. Note: on .NET 11 this runs the server GC with **DATAS** (Dynamic Adaptation to Application Sizes, the default server-GC mode since .NET 9), so the measurements above reflect the DATAS mode.
- Removed app-side induced `GC.Collect(blocking:false)` calls: no change to the pattern.

---

*This issue report was generated with the assistance of an AI assistant.*
