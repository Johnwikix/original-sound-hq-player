# 🎵 原音 HQ 播放器（Original Sound HQ Player）🎶

这是一款采用 WinUI 构建的现代化多功能音乐播放器应用，旨在为 Windows 系统提供流畅愉悦的音乐聆听体验。该应用支持用户浏览音乐库、管理播放列表、发现新音乐，并能实现高品质音频播放。它依托依赖注入、日志记录和 MVVM 架构等现代.NET 技术，打造出可维护且具备可扩展性的代码库。

[点击此处访问产品主页](https://johnwikix.github.io/original-sound-player-page)

## 🚀 核心功能

- **音乐库浏览**：可按歌曲、艺术家、专辑、文件夹或收藏夹智能分类，一键快速定位心仪音乐；支持手动添加本地文件夹扫描，自动重新扫描功能实时更新音乐库，处理文件变动。
- **收藏播放列表**：创建并管理包含个人喜爱歌曲的自定义播放列表，支持将歌曲添加至单独的“最爱音频列表”。
- **播放控制**：提供完整播放控制功能，包括播放、暂停、切歌、随机播放、顺序播放和单曲循环；支持键盘快捷键（Esc 返回、空格播放/暂停、← 快退 5s、→ 快进 5s 等）。
- **音频处理**：支持 DSD、FLAC、WAV、MP3 等 12 种以上音乐格式；可将音频转换为 WAV、MP3、FLAC、OGG、OPUS 格式；集成自定义十段均衡器及多种预设。
- **音乐信息展示**：播放界面实时呈现歌曲标题、创作者、专辑名、时长、采样率、码率、文件类型；支持配置 LRCAPI 源匹配专辑封面与歌词，播放页按时间戳滚动显示歌词。
- **设备支持**：支持通过 USB 将匹配元信息的音乐（含歌词）传输到 Sony Walkman；扫描并导入已连接 USB 设备中的音乐。
- **现代化体验**：采用 WinUI 构建简洁直观的界面，支持云母、亚克力等 3 种应用样式，系统默认、深色、浅色 3 种主题；集成 SMTC 系统媒体传输控件，显示专辑原始封面与时间轴。
- **基础功能**：可自定义动画时间、初始界面；支持单实例运行，防止窗口被调整至过小尺寸；使用 SQLite 存储音乐库和播放列表数据。

## 🎧 音频输出模式

提供多种专业音频输出方案，适配不同音质需求：

- **WASAPI 模式**：独占模式（支持推送/事件）减少系统干扰，降低延迟；共享模式可与其他应用共享音频设备。
- **DirectSound 模式**：具备硬件加速能力，提升复杂音频或多声道音频的播放效率。
- **DSD 输出**：支持 DSD DoP（封装为 PCM 帧）和 DSD Native（通过 ASIO 原始输出）两种方式。
- **ASIO 支持**：原生支持 ASIO 输出，适配专业音频设备。

## 📦 下载与安装

**原音 HQ 播放器** 已上架 Microsoft Store，推荐通过官方商店获取，以确保最佳的安装和更新体验。

<p align="center">
  <a href="https://apps.microsoft.com/detail/9NFW1RPPT999?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
  </a>
</p>

## 🛠️ 第三方库

| Library                                                                                                  | Description                          | License            |
| -------------------------------------------------------------------------------------------------------- | ------------------------------------ | ------------------ |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon)                                            | 系统托盘图标                         | MIT                |
| [WinUIEx](https://github.com/dotMorten/WinUIEx)                                                          | 扩展 WinUI 窗口功能                   | MIT                |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)                                      | MVVM 框架                            | MIT                |
| [DevWinUI](https://github.com/ghost1372/DevWinUI) | WinUI 扩展组件                       | MIT                |
| [TagLibSharp](https://github.com/mono/taglib-sharp)和[atldotnet](https://github.com/Zeugma440/atldotnet) | 读取并处理多种音频格式的元数据        | LGPL-2.1/MIT       |
| [BASS](https://www.un4seen.com/)和[ManagedBass](https://github.com/ManagedBass/ManagedBass)              | 用于音乐播放的音频库                 | non-commercial use |
| [Lyricify.Lyrics.Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) | 歌词搜索与解析                       | MIT                |
| [Isolation](https://github.com/Storyteller-Studios/Isolation) | 着色器流体背景 | MIT |

## 代码参考

- [BetterLyrics](https://github.com/jayfunc/BetterLyrics)
- [WindowsMusicPlayer-TheUntamedMusicPlayer](https://github.com/LanZhan-Harmony/WindowsMusicPlayer-TheUntamedMusicPlayer)

## 📦 快速开始

按照以下步骤操作，可在本地计算机上启动并运行该项目。

### 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)（.NET 10 软件开发工具包）
- Windows 10 19041 版本或更高版本（需适配系统要求）
- Visual Studio 2026 或更高版本（需安装 WinUI 工作负载）

### 安装步骤

1. 克隆代码仓库：

   ```bash
   git clone https://github.com/Johnwikix/original-sound-hq-player.git
   ```

2. 还原 NuGet 包：

   - 在“解决方案资源管理器”中，右键点击解决方案。
   - 选择“还原 NuGet 包”。

### 本地运行与初始设置

1. 构建解决方案：

   - 按下 `Ctrl+Shift+B` 组合键，或从“生成”菜单中选择“生成解决方案”。

2. 运行应用程序：

   - 按下 `Ctrl+F5` 组合键，或从“调试”菜单中选择“开始执行（不调试）”。

3. 首次使用配置：

   - 启动后手动添加包含音乐文件的文件夹，完成扫描导入以构建音乐库。
   - 通过侧边栏导航切换播放列表页面，点击“添加文件夹”可继续扩充音乐库。

## 📸 截图展示

![example](doc/img/1.png)
![example](doc/img/2.png)
![example](doc/img/3.png)
![example](doc/img/4.png)
![example](doc/img/5.png)
![example](doc/img/6.png)
![example](doc/img/7.png)
![example](doc/img/8.png)
![example](doc/img/9.png)
![example](doc/img/10.png)

## 存储位置

应用程序数据存储在以下目录中：

- 用户数据: `%userprofile%\documents\OriginalSoundPlayer`

## 🤝 贡献指南

欢迎参与项目贡献！如果您有想法、错误报告或功能请求，请打开问题或提交拉取请求。

## 📝 许可证

本项目基于 [MIT 许可证](LICENSE) 授权。

## 📬 联系方式

- 问题反馈交流 QQ 群：1009034363
- 邮箱：[dannypan9709@foxmail.com](dannypan9709@foxmail.com)

## 💖 致谢

感谢您关注原音 HQ 播放器！希望这款应用能为您带来实用与愉悦的使用体验。
