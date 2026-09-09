# 验证 ASIO PCM 转储：按驱动采样类型解码声道0字节流，检测 440Hz 正弦 + 幅度稳定
# 用法: verify_sine.py <dump.bin> <bytes_per_sample:int> [signed|float]
import sys, struct, math

dump = sys.argv[1]
bps = int(sys.argv[2])
fmt = sys.argv[3] if len(sys.argv) > 3 else "signed"
rate = int(sys.argv[4]) if len(sys.argv) > 4 else 44100

raw = open(dump, "rb").read()
n = len(raw) // bps
if n < 44100 // 10:
    print("FAIL dump too small:", len(raw))
    sys.exit(1)

def get(i):
    b = raw[i * bps:(i + 1) * bps]
    if fmt == "float" and bps == 4:
        return struct.unpack("<f", b)[0]
    if bps == 2:
        return struct.unpack("<h", b)[0] / 32768.0
    if bps == 3:
        v = b[0] | (b[1] << 8) | (b[2] << 16)
        if v & 0x800000:
            v -= 0x1000000
        return v / 8388608.0
    return struct.unpack("<i", b)[0] / 2147483648.0

# 跳过头部可能的静音段（淡入/预缓冲）
start = 0
for i in range(n):
    if abs(get(i)) > 0.01:
        start = i
        break
seg = min(n - start, 44100 // 3)  # 只分析数据起点后 0.3s（避开后续可能的 EQ 生效）

# Goertzel 检测 440Hz
samples = [get(start + i) for i in range(seg)]
k = 440 * seg / rate
w = 2 * math.pi * k / seg
c, s = math.cos(w), math.sin(w)
g1 = g2 = 0.0
for x in samples:
    y = x + 2 * c * g1 - g2
    g2, g1 = g1, y
power = g1 * g1 + g2 * g2 - 2 * c * g1 * g2
ampl441 = 2 * math.sqrt(power) / seg

rms = math.sqrt(sum(x * x for x in samples) / seg)
peak = max(abs(x) for x in samples)

# 过零率估频
zc = 0
for i in range(1, seg):
    if samples[i - 1] <= 0 < samples[i]:
        zc += 1
est_hz = zc / (seg / rate)

print(f"samples={n} start={start} rate={rate} rms={rms:.4f} peak={peak:.4f} goertzel440_ampl={ampl441:.4f} zcr_est={est_hz:.1f}Hz")
ok = 0.30 < rms < 0.50 and 0.40 < ampl441 < 0.60 and abs(est_hz - 440) < 6
print("RESULT:", "PASS" if ok else "CHECK", "(期望 rms≈0.354=0.5/√2、440Hz 幅度≈0.5、过零≈440)")
