# 实验性 5.1、Atmos HDMI 与进度快照

设置 → 输出 →「实验性 5.1 输出」，默认关闭。只有开启且使用 ASIO 或 WASAPI
独占（Push/Event）的标准六声道 PCM 才启用新路径。支持 `5.1`（FL/FR/FC/LFE/BL/BR，
掩码 0x3F）与 `5.1(side)`（FL/FR/FC/LFE/SL/SR，0x60F），保留解码布局和声道顺序。
WASAPI 将布局带入每种候选 PCM 格式的 WAVEFORMATEXTENSIBLE；布局变化必须重建设备缓冲。

ASIO 使用驱动前六个输出通道，顺序固定为左、右、中置、LFE、左环绕、右环绕。
驱动通道序号不代表扬声器位置，实际接线/驱动路由必须与上述顺序匹配；当前没有自动识别
或自定义映射。驱动少于六路时拒绝实验性六声道输出，沿用已有共享模式失败回退。
此 5.1 PCM 选项不渲染 Atmos 对象；Atmos HDMI 位流由下面的独立选项启用。

关闭开关保留原解码规范化、声道数量和输出协商行为。共享模式（含 DirectSound）、
单/双声道、DSD/DoP 和其它声道布局不进入实验路径。现有多声道 EQ/音效限制不变。
没有 5.1 设备，尚未验证实机驱动协商、音箱位置及听感；不得以软件回归替代硬件认证。

`PlaybackSwitchRegression` 使用六路独立信号验证解码样本、布局和 WASAPI 字节步长，
验证 ASIO 六路写入顺序、设备通道不足、布局变更禁止复用；关闭开关以及单/双声道、
DSD 仍由原有与新增回归覆盖。

## 实验性 Atmos HDMI 直通

独立开关「实验性 Atmos HDMI 直通」，默认关闭。初版支持 48 kHz、六声道 E-AC-3
（Dolby Digital Plus，包括 JOC/Atmos）文件，优先于独占模式的 PCM 路径。
仅 WASAPI 独占 Push/Event 可用；ASIO 与共享输出保持原行为。不支持 TrueHD/MAT、
AC-4、耳机 Atmos 或软件对象渲染，也不会把普通 5.1 PCM 伪装成 Atmos。

解复用后将原始音轨打包为 IEC 61937，保留 JOC 元数据，不解码/重编码。每个载波
突发为 24,576 字节，192 kHz / 2 通道 / 16 bit；这两个通道是 HDMI 传输载波，
不是立体声下混。WASAPI 使用完整的 52 字节 IEC 格式结构，先尝试 Atmos DD+ 子类型，
驱动不支持时尝试通用 DD+ 子类型；两者传输完全相同的音轨字节。
压缩数据绝不作为 PCM/DoP/ASIO 采样发送。设备拒绝位流时沿用原有重新解码为 PCM
并回退共享的流程。硬件必须是支持相应格式的 HDMI/DisplayPort 接收端及兼容功放。

位流模式绕过应用音量、静音、淡入淡出及所有 DSP，音量由功放控制；音效页会显示
独立的 E-AC-3/Atmos 位流提示。暂停保留载波缓冲；停止/seek 释放旧输出，再从新位置
重建载波，避免拼接半个旧数据包。定位为压缩帧粒度；容器预滚、接收端解码延迟和
声画同步未经实机验证，不承诺采样精确 seek 或功放一定显示 Atmos。

验证：合成 E-AC-3 与用户提供的 Atmos M4A 都逐字节对照独立 FFmpeg `-c:a copy -f spdif`
输出。用户文件共 6,646 个突发，SHA256 为
`75EDA341545BEDFD5855394297768C639189EC7265AA3A17607FDE414BB8F1BA`。
回归还检查 ring 字节保真、EOF/seek、格式尾部跨线程复制、输出直写、默认关闭及模式隔离。
没有实际设备，HDMI 驱动协商、功放识别、天空声道和听感仍未验证。

依据：[微软 IEC 61937 格式定义](https://learn.microsoft.com/en-us/windows/win32/coreaudio/representing-formats-for-iec-61937-transmissions)、
FFmpeg n9.0.1 `libavformat/spdifenc.c` 的 E-AC-3 打包行为。回归基准在本机使用：

```powershell
ffmpeg -i "track.m4a" -map 0:a:0 -c:a copy -f spdif reference.spdif
dotnet run --project _tools/PlaybackSwitchRegression -- --test-atmos-file "track.m4a" reference.spdif
```

## 进度与逐字歌词

UI 的进度轮询不再发 `GetTimeProgress` 请求。播放子进程每 50 ms 在独立
`AudioPlayer_Progress_v1` 共享内存发布最新快照：版本、时间线代次、当前位置、总长、
Stopwatch 时间戳、播放状态及已处理 seek ID。发布线程只尝试获取引擎控制锁，设备重建
忙时跳过；不在实时音频回调中分配内存、等待或写入 IPC。旧请求接口保留给诊断工具。

共享内存使用单写者版本校验，读者有界重试，不排队、不等待响应。UI 每 50 ms 读取，
进度条和逐字歌词使用同一个 PlaybackTimeline；歌词不再对离散 UI 事件独立累加并硬校准。
同代次只前进，最多外推 100 ms；设备停滞时停止推算，避免歌词无限超前。换曲/seek
更新代次允许真正的后退，连续 seek 的旧确认不能覆盖最新目标。停止/暂停冻结时钟。

必须同时部署新主程序与 `Player/AudioPlayer.exe`。测试包括延迟/重复快照、停滞、暂停、
结束、向后 seek、连续 seek、换曲、旧 seek 协议兼容，以及跨进程快照完整性。
