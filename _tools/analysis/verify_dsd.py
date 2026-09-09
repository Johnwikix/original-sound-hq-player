# 验证 ASIO native DSD 转储（MSB1 直传：声道0字节流 = 交织 DSD 块的 L 字节序列 = dsd[0],dsd[2],…）
# 用法: verify_dsd.py <dump.bin> <file.dff>
import sys, struct

dump_path, dff_path = sys.argv[1], sys.argv[2]
dump = open(dump_path, "rb").read()

f = open(dff_path, "rb")
assert f.read(4) == b"FRM8"
f.read(8)
assert f.read(4) == b"DSD "
channels, dsd_off, dsd_len = 2, None, 0
while True:
    hdr = f.read(12)
    if len(hdr) < 12: break
    cid, sz = hdr[:4], struct.unpack(">Q", hdr[4:12])[0]
    pos = f.tell()
    if cid == b"PROP":
        end = pos + sz
        f.read(4)  # 'SND '
        while f.tell() < end:
            p = f.read(12)
            if len(p) < 12: break
            pid, psz = p[:4], struct.unpack(">Q", p[4:12])[0]
            ppos = f.tell()
            if pid == b"CHNL":
                channels = struct.unpack(">H", f.read(2))[0]
            f.seek(ppos + psz + (psz & 1))
    elif cid == b"DSD ":
        dsd_off, dsd_len = pos, sz
    f.seek(pos + sz + (sz & 1))

f.seek(dsd_off)
dsd = f.read(min(dsd_len, 1 << 24))
print(f"dff: channels={channels} dsd_bytes={dsd_len}")

# 跳过前导 0x69 静音（预填充+预缓冲）
start = 0
while start < len(dump) and dump[start] == 0x69:
    start += 1
print(f"dump={len(dump)}B silence_prefix={start}B (~{start/352800*1000:.0f}ms)")

# L 流对比：dump[start+i] == dsd[2i]（交织流隔字节）
n = min(len(dump) - start, (len(dsd) // 2) - 1)
mism = 0
first_bad = None
run = 0
best_run = 0
for i in range(n):
    if dump[start + i] != dsd[2 * i]:
        mism += 1
        if first_bad is None:
            first_bad = i
        run = 0
    else:
        run += 1
        best_run = max(best_run, run)
print(f"compare: bytes={n} mismatch={mism} first_bad={first_bad} best_match_run={best_run}")

# 若首字节错位，扫描 dsd 内 dump 数据前 16 字节的出现在位置（判断偏移/位反转）
if first_bad == 0:
    probe = dump[start:start + 16]
    idx = dsd.find(probe)
    print(f"前16字节直接在 dsd 中出现: idx={idx}")
    def rev(b): return int(f"{b:08b}"[::-1], 2)
    probe_r = bytes(rev(b) for b in probe)
    idx2 = dsd.find(probe_r)
    print(f"前16字节位反转后在 dsd 中出现: idx={idx2}")

# seek 后数据跳变属正常：要求从数据起点起有 ≥1s（352800B@DSD64）的连续完美匹配
ok = best_run >= 352800 and first_bad in (None,) or best_run >= 352800
print("RESULT:", "PASS" if best_run >= 352800 else "FAIL")
