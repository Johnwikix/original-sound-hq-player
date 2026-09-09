# 验证 WASAPI DoP 转储（24packed：帧字节 = [第二DSD字节, 第一DSD字节, 标记]）：
#   1) 标记 0x05/0xFA 全程严格交替（含 6615 奇数缓冲边界——相位回归核心）
#   2) 数据字节与 .dff 的 DSD 块交织流逐字节一致
# 用法: verify_dop.py <dump.bin> <file.dff>
# DSDIFF 布局（本文件实测）：所有块 8 字节大端尺寸。FRM8|8B size|'DSD '|块…
# 块：FVER / PROP('SND ' 内含 'FS  '(采样率) 'CHNL'(声道)) / DSD(交织 DSD 数据)
import sys, struct

dump_path, dff_path = sys.argv[1], sys.argv[2]
dump = open(dump_path, "rb").read()

f = open(dff_path, "rb")
assert f.read(4) == b"FRM8", "not DSDIFF"
f.read(8)  # FRM8 总尺寸
assert f.read(4) == b"DSD "
channels, fs, dsd_off, dsd_len = 2, 2822400, None, 0
while True:
    hdr = f.read(12)
    if len(hdr) < 12: break
    cid, sz = hdr[:4], struct.unpack(">Q", hdr[4:12])[0]
    pos = f.tell()
    print(f"chunk {cid.decode('latin1')}: @{pos} size={sz}")
    if cid == b"PROP":
        end = pos + sz
        f.read(4)  # 'SND '
        while f.tell() < end:
            p = f.read(12)
            if len(p) < 12: break
            pid, psz = p[:4], struct.unpack(">Q", p[4:12])[0]
            ppos = f.tell()
            if pid == b"FS  ":
                fs = struct.unpack(">I", f.read(4))[0]
            elif pid == b"CHNL":
                channels = struct.unpack(">H", f.read(2))[0]
            f.seek(ppos + psz + (psz & 1))
    elif cid == b"DSD ":
        dsd_off, dsd_len = pos, sz
    f.seek(pos + sz + (sz & 1))

if dsd_off is None:
    print("FAIL: no DSD chunk")
    sys.exit(1)
f.seek(dsd_off)
dsd = f.read(min(dsd_len, 1 << 24))
print(f"dff: channels={channels} fs={fs} dsd_bytes={dsd_len} (byte rate/ch = {fs//8})")

# ── DoP 帧解析 ──
frames = len(dump) // (3 * channels)
markers = []
data_l = []
for i in range(frames):
    base = i * 3 * channels
    markers.append(dump[base + 2])
    data_l.append((dump[base + 1], dump[base]))  # (第一 DSD 字节, 第二 DSD 字节) L 声道

# 1) 标记交替（缓冲边界绝不允许翻转；会话重置[seek/换曲]允许重新从 0x05 起相）
breaks = [i for i in range(len(markers) - 1)
          if markers[i] == markers[i + 1] or markers[i] not in (0x05, 0xFA)]
first_run = breaks[0] if breaks else len(markers)
alt_ok = first_run >= 1000
print(f"markers: first 8 = {[hex(m) for m in markers[:8]]} total={frames} 首段连续交替={first_run} 帧, 断点={breaks[:6]}")

# 2) 找数据起点（跳过 0x6969 静音）
start = 0
while start < frames and data_l[start] == (0x69, 0x69):
    start += 1
print(f"data starts at frame {start} (silence frames={start})")

# 3) 与 DSD 块对比：字节交织 L R L R…，DoP 帧 f 的 L=(dsd[4f],dsd[4f+2]) R=(dsd[4f+1],dsd[4f+3])；
#    对比止于首个标记断点（seek/MusicEnd 后数据跳变属正常）
limit = min(first_run - start, (len(dsd) - 3) // 4, frames - start)
mism = mism_r = 0
first_bad = None
for i in range(max(0, limit)):
    b = i * 6  # 转储帧：L3 + R3（24packed LE）
    l1, l0, r1, r0 = dump[b + 1], dump[b], dump[b + 4], dump[b + 3]
    if l1 != dsd[4 * i] or l0 != dsd[4 * i + 2] or r1 != dsd[4 * i + 1] or r0 != dsd[4 * i + 3]:
        mism += 1
        if first_bad is None:
            first_bad = i
checked = max(0, limit)
print(f"payload compare: frames={checked} mismatch={mism}" + (f" first_bad={first_bad}" if first_bad is not None else ""))

ok = alt_ok and start < frames and checked > 1000 and mism == 0
print("RESULT:", "PASS" if ok else "FAIL")
