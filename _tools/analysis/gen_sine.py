# 生成 ASIO 格式验证用正弦文件：44100Hz 16bit 立体声 440Hz 10s，幅度 0.5
import wave, math, struct

rate, dur, freq, amp = 44100, 10, 440, 0.5
out = r"D:\audioTest\sine440.wav"
with wave.open(out, "w") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(rate)
    frames = bytearray()
    n = rate * dur
    for i in range(n):
        v = int(amp * math.sin(2 * math.pi * freq * i / rate) * 32767)
        frames += struct.pack("<hh", v, v)
    w.writeframes(bytes(frames))
print("wrote", out, n, "frames")
