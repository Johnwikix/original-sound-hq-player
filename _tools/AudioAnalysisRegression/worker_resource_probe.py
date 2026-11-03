"""Bounded resource observation of the actual shipped worker (local read-only music)."""
import json, subprocess, time, hashlib, statistics
from pathlib import Path
import ctypes
from ctypes import wintypes as W
from types import SimpleNamespace
kernel=ctypes.WinDLL("kernel32",use_last_error=True)
psapi=ctypes.WinDLL("psapi",use_last_error=True)
class Memory(ctypes.Structure):
    _fields_=[("cb",W.DWORD),("faults",W.DWORD)]+[(name,ctypes.c_size_t) for name in ["peak_wset","rss","peak_paged","paged","peak_nonpaged","nonpaged","pagefile","peak_pagefile","private"]]
kernel.OpenProcess.argtypes=[W.DWORD,W.BOOL,W.DWORD];kernel.OpenProcess.restype=W.HANDLE
kernel.CloseHandle.argtypes=[W.HANDLE]
kernel.GetProcessHandleCount.argtypes=[W.HANDLE,ctypes.POINTER(W.DWORD)]
kernel.GetProcessTimes.argtypes=[W.HANDLE]+[ctypes.POINTER(ctypes.c_uint64)]*4
psapi.GetProcessMemoryInfo.argtypes=[W.HANDLE,ctypes.POINTER(Memory),W.DWORD]
class Observation:
    def __init__(self,pid):
        self.handle=kernel.OpenProcess(0x410,False,pid)
        if not self.handle:raise ctypes.WinError(ctypes.get_last_error())
    def memory_info(self):
        result=Memory();result.cb=ctypes.sizeof(result)
        if not psapi.GetProcessMemoryInfo(self.handle,ctypes.byref(result),result.cb):raise ctypes.WinError(ctypes.get_last_error())
        return result
    def cpu_times(self):
        values=[ctypes.c_uint64() for _ in range(4)]
        if not kernel.GetProcessTimes(self.handle,*map(ctypes.byref,values)):raise ctypes.WinError(ctypes.get_last_error())
        return SimpleNamespace(user=values[3].value/1e7,system=values[2].value/1e7)
    def num_handles(self):
        value=W.DWORD();kernel.GetProcessHandleCount(self.handle,ctypes.byref(value));return value.value
    def close(self):kernel.CloseHandle(self.handle)

root=Path.cwd()
exe=root/"Player/AudioAnalysisWorker.exe"
# Run in an isolated flat deployment exactly as the application copies its executable/DLLs.
run=root/"_tools/artifacts/analysis-worker-probe"
run.mkdir(parents=True,exist_ok=True)
import shutil
shutil.copy2(exe,run/exe.name)
for dll in (root/"Libraries/FFmpeg/x64").glob("*.dll"):shutil.copy2(dll,run/dll.name)
tracks=json.loads((root/"_tools/artifacts/real-music-comparison/manifest.json").read_text(encoding="utf-8"))["Tracks"]
process=subprocess.Popen([str(run/exe.name)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True,encoding="utf-8",creationflags=subprocess.CREATE_NO_WINDOW)
observed=Observation(process.pid)
points=[];count=0;start=time.monotonic()
try:
    while time.monotonic()-start<180:
        track=tracks[count%len(tracks)]
        process.stdin.write(json.dumps({"Path":track["Path"]})+"\n");process.stdin.flush()
        line=process.stdout.readline()
        if not line:raise RuntimeError("worker exited")
        reply=json.loads(line)
        if len(reply.get("Features") or [])!=384:raise RuntimeError(reply.get("Error"))
        count+=1
        m=observed.memory_info();cpu=observed.cpu_times()
        points.append({"seconds":time.monotonic()-start,"completed":count,"cpu_seconds":cpu.user+cpu.system,"private":m.private,"working_set":m.rss,"peak_working_set":m.peak_wset,"peak_commit":m.peak_pagefile,"handles":observed.num_handles()})
    wall=time.monotonic()-start
    last=points[-1]
    early=[p["private"] for p in points if 60<=p["seconds"]<120]
    late=[p["private"] for p in points if 120<=p["seconds"]<=180]
    result={"worker_sha256":hashlib.sha256(exe.read_bytes()).hexdigest(),"wall_seconds":wall,"completed":count,"one_core_cpu_percent":last["cpu_seconds"]/wall*100,"peak_working_set_mib":max(p["peak_working_set"] for p in points)/1048576,"peak_commit_mib":max(p["peak_commit"] for p in points)/1048576,"private_window_delta_mib":(statistics.median(late)-statistics.median(early))/1048576,"handle_range":[min(p["handles"] for p in points),max(p["handles"] for p in points)],"scope":"Actual NativeAOT worker, 10% CPU duty, sequential real files, 3x10s each; 180s observation, not proof of no leaks; native allocation volume unmeasured.","points":points}
    process.stdin.close();process.wait(timeout=25)
    result["exit_code"]=process.returncode
    target=root/"_tools/artifacts/analysis-worker-resources.json"
    target.write_text(json.dumps(result,indent=2),encoding="utf-8",newline="\r\n")
    print(json.dumps({k:v for k,v in result.items() if k!="points"},indent=2),flush=True)
finally:
    observed.close()
    if process.poll() is None:process.kill();process.wait()
