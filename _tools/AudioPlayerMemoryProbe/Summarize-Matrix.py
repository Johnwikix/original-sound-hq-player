"""Summarize MemoryMatrixProbe JSON and the diagnostic child's stdout separately."""
import csv
import json
from pathlib import Path
import re
import statistics
import sys


def summarize(path):
    run = json.loads(path.read_text(encoding="utf-8-sig"))
    phase = "startup"
    memory, pauses, kinds = [], [], []
    runtime = ""
    for line in path.with_suffix(".log").read_text(encoding="utf-8-sig", errors="replace").splitlines():
        if line.startswith("[matrix-phase] "):
            phase = line.split(" ", 1)[1].split(",")[0]
        elif line.startswith("[runtime] "):
            runtime = line[10:]
        elif re.match(r"\[memory\] \d+,", line):
            values = [int(v) for v in line[9:].split(",")]
            memory.append(dict(zip(["Ms", "Allocated", "Heap", "Committed", "Fragmented", "Gen0", "Gen1", "Gen2",
                                    "GcHeapSize", "Loh", "LohFragmented", "FinalizationPending"], values), Phase=phase))
        elif line.startswith("[gc-pause] "):
            values = [float(v) for v in line[11:].split(",")]
            pauses.append(dict(zip(["Ms", "Total", "Index", "Pause0", "Pause1"], values), Phase=phase))
        elif line.startswith("[gc-kind] "):
            values = line[10:].split(",")
            kinds.append(dict(Phase=phase, Kind=values[1], Index=int(values[2]), Generation=int(values[3]),
                              Concurrent=values[4] == "True", Compacted=values[5] == "True", Heap=int(values[6]),
                              Promoted=int(values[7]), FinalizationPending=int(values[8]),
                              Pause0=float(values[9]), Pause1=float(values[10])))
    measured = [s for s in run["Samples"] if s["Phase"] == "playing"]
    natural = [s for s in memory if s["Phase"] == "measuring"]
    natural_pauses = [s for s in pauses if s["Phase"] == "measuring"]
    result = dict(Name=path.stem, Status=run["Status"], Transitions=run["Transitions"], Runtime=runtime,
                  RequestedMode=run["Mode"], ActualOutput=",".join(sorted({str(s["Output"]) for s in measured})),
                  SampleRates=",".join(str(v) for v in sorted({s["SampleRate"] for s in measured if s["SampleRate"] is not None})),
                  DurationSeconds=measured[-1]["Seconds"] - measured[0]["Seconds"],
                  WorkingSetMeanMiB=statistics.mean(s["WorkingSet"] for s in measured) / 2**20,
                  WorkingSetPeakMiB=max(s["WorkingSet"] for s in measured) / 2**20,
                  PrivatePeakMiB=max(s["PrivateBytes"] for s in measured) / 2**20,
                  WorkingSetLastMiB=measured[-1]["WorkingSet"] / 2**20,
                  PrivateFirstMiB=measured[0]["PrivateBytes"] / 2**20,
                  PrivateLastMiB=measured[-1]["PrivateBytes"] / 2**20,
                  HandlesFirst=measured[0]["Handles"], HandlesLast=measured[-1]["Handles"],
                  ThreadsFirst=measured[0]["Threads"], ThreadsLast=measured[-1]["Threads"])
    if run["Mode"] == "ASIO" and any(s["Output"] != "Asio" for s in measured):
        result["Status"] = "OUTPUT_FALLBACK"
    if natural:
        first, last = natural[0], natural[-1]
        elapsed = (last["Ms"] - first["Ms"]) / 1000
        result.update(ManagedWindowSeconds=elapsed,
                      AllocationKiBPerSec=(last["Allocated"] - first["Allocated"]) / elapsed / 1024,
                      HeapFirstMiB=first["Heap"] / 2**20, HeapLastMiB=last["Heap"] / 2**20,
                      HeapPeakMiB=max(s["Heap"] for s in natural) / 2**20,
                      CommittedPeakMiB=max(s["Committed"] for s in natural) / 2**20,
                      LohAfterLastGcMiB=last["Loh"] / 2**20,
                      FragmentedPeakMiB=max(s["Fragmented"] for s in natural) / 2**20,
                      Gen0=last["Gen0"]-first["Gen0"], Gen1=last["Gen1"]-first["Gen1"], Gen2=last["Gen2"]-first["Gen2"])
        if natural_pauses:
            observed = [s for s in kinds if s["Phase"] == "measuring"
                        and natural_pauses[0]["Index"] < s["Index"] <= natural_pauses[-1]["Index"]]
            result.update(PauseTotalMs=natural_pauses[-1]["Total"] - natural_pauses[0]["Total"],
                          PauseObservedMaxMs=max((max(s["Pause0"], s["Pause1"]) for s in observed), default=0))
        collected = [s for s in memory if s["Phase"] == "explicit-gc"]
        if collected:
            result.update(StoppedCollectedHeapMiB=collected[-1]["Heap"] / 2**20,
                          StoppedCollectedCommittedMiB=collected[-1]["Committed"] / 2**20)
        after_off = [s for s in memory if s["Phase"] == "stopped-dsp-off"]
        if collected and after_off:
            result["StoppedDspOffCollectedHeapMiB"] = after_off[-1]["Heap"] / 2**20
    result["PostStop"] = [s for s in run["Samples"] if s["Phase"] != "playing"]
    result["ObservedCollections"] = [s for s in kinds if s["Phase"] == "measuring"]
    return result, dict(Name=path.stem, Process=run["Samples"], Memory=memory, Pauses=pauses)


def main():
    root = Path(sys.argv[1])
    results, series = [], []
    for path in sorted(root.glob("*.json")):
        if not path.with_suffix(".log").exists():
            continue
        result, samples = summarize(path)
        results.append(result)
        series.append(samples)
    (root / "summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    (root / "timeseries.json").write_text(json.dumps(series, indent=2), encoding="utf-8")
    flat = [{k:v for k,v in row.items() if not isinstance(v, list)} for row in results]
    columns = list(dict.fromkeys(k for row in flat for k in row))
    with (root / "summary.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, columns)
        writer.writeheader()
        writer.writerows(flat)
    print("Scenario         WS peak  Private  Alloc KiB/s   GC 0/1/2   Pause ms  Collected heap MiB")
    for row in results:
        print(f"{row['Name']:16} {row['WorkingSetPeakMiB']:7.2f} {row['PrivatePeakMiB']:8.2f} "
              f"{row.get('AllocationKiBPerSec', 0):11.2f} "
              f"{str([row.get('Gen0'),row.get('Gen1'),row.get('Gen2')]):>12} "
              f"{row.get('PauseTotalMs',0):9.3f} {row.get('StoppedCollectedHeapMiB',0):12.3f}")


if __name__ == "__main__":
    main()
