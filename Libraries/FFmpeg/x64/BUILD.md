# FFmpeg 构建记录（2026-09-20）

本次为在线播放接口按需重建；后续按需重建授权见根目录 `AGENTS.md`。

- 源码：`G:\SoftwareProject\ffmpeg\src`，tag `n9.0.1`。
- 独立源码工作树：`G:\SoftwareProject\ffmpeg\network-src`。
- 构建/安装：`G:\SoftwareProject\ffmpeg\build-network`、`install-network`。
- 工具链：`G:\msys64` UCRT64；完整 configure 参数与步骤见 `build/ffmpeg-network.sh`。
- Windows TLS 使用系统 Schannel，播放器强制验证证书；运行时协议白名单限制为 HTTP/HTTPS 及底层连接协议。
- 保留原有音频编解码功能（含 AC3/EAC3、DSD、WavPack、转换编码器）；未启用 HLS 或 DRM。
- DLL 新增系统依赖：Secur32/Crypt32/WS2_32；无需额外分发 TLS 库。

## 源码提交

bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa

## SHA-256

- `avcodec-63.dll`：`c542ef2674dce8019cf13313d2b91fbdd9ba0b321f7b8a347407050eeb55f27c`
- `avformat-63.dll`：`2dd8aaebf5fad9bed16d5b32fe11b7adf810c3c9544e25a81e9432566b158fac`
- `avutil-61.dll`：`7e831891ab03fb20eddd8ff57a1967bb8c694c35d974369ba22be570f4473169`
- `swresample-7.dll`：`44b35ea4483c3efa517c827ceee3bf8fe65fe514690a1d3aa6e55eb113e8d940`
