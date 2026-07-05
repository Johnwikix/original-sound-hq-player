**English** | [**中文**](README.md)

<div align="center">
  <img src="Assets/Music.png" alt="Logo" width="120">
  
  <h1>OriginalSound HI-FI Player</h1>

  <h3>原音 HQ 播放器</h3>

  <h4>
    A modern, high-fidelity music player built with WinUI 3<br>
    Designed for Windows desktop, focused on lossless audio and an immersive listening experience
  </h4>

  <div>
    <img src="https://img.shields.io/badge/Language-C%23-purple" alt="C#">
    <img src="https://img.shields.io/badge/Framework-WinUI%203-blue" alt="WinUI 3">
    <img src="https://img.shields.io/badge/License-MIT-blue" alt="License">
    <a href="https://github.com/Johnwikix/original-sound-hq-player/stargazers"><img src="https://img.shields.io/github/stars/Johnwikix/original-sound-hq-player" alt="Star"></a>
    <a href="https://github.com/Johnwikix/original-sound-hq-player/releases/latest"><img src="https://img.shields.io/github/downloads/Johnwikix/original-sound-hq-player/total?label=Downloads" alt="Downloads"></a>
  </div>

  <br>

</div>

<br>

<div align="center">

[**🏠 Product Page**](https://johnwikix.github.io/original-sound-player-page) | [**🐞 Report Issue**](https://github.com/Johnwikix/original-sound-hq-player/issues)

</div>

<br>

## 📥 Download & Install

<div align="center">

| Microsoft Store (Recommended) |
| :---: |
| <a href="https://apps.microsoft.com/detail/9NFW1RPPT999?referrer=appbadge&mode=direct"><img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/></a><br>Get the best installation and update experience from the Microsoft Store |

</div>

## 🌟 Features

- 🎵 **Music Library Browsing**
  - Multi-dimensional browsing by song, artist, album, folder, or favorites
  - Manual local folder scanning with automatic rescan to keep your library in sync
  - Quick access to your favorite tracks

- 📂 **Favorites & Playlists**
  - Create and manage custom playlists
  - A dedicated "Favorite Audio List" for quick access

- 🎛️ **Full Playback Control**
  - Play, pause, skip, shuffle, sequential, single-loop
  - Customizable global shortcuts

- 🔊 **Professional Audio Processing**
  - Supports 12+ audio formats including DSD, FLAC, WAV, MP3
  - Audio conversion: WAV, MP3, FLAC, OGG, OPUS
  - Built-in 10-band equalizer with multiple presets

- 📝 **Music Info & Lyrics**
  - Real-time display of title, artist, album, duration, sample rate, bitrate, file type
  - Automatic album art and lyrics matching
  - Advanced per-word animated lyrics with multiple shader background options

- 📱 **Sony Walkman Support**
  - Transfer music with metadata (including lyrics) to Sony Walkman over USB
  - Scan and import music from connected USB devices

- 🪟 **Modern UI Experience**
  - Clean and intuitive interface built with WinUI 3
  - 3 application styles: Mica, Acrylic, and more
  - 3 themes: System Default, Dark, Light
  - Integrated SMTC (System Media Transport Controls)
  - Original album art with timeline display

- 🗃️ **Data & Core Capabilities**
  - SQLite-powered music library and playlists
  - Single-instance enforcement with minimum window size guard

## 🎵 Audio Output Modes

Professional audio output options to match different quality needs:

- **WASAPI Mode**
  - Exclusive mode (push/event) reduces system interference and lowers latency
  - Shared mode allows sharing the audio device with other apps

- **DirectSound Mode**
  - Hardware-accelerated playback for complex or multi-channel audio

- **DSD Output**
  - DSD DoP (encapsulated into PCM frames)
  - DSD Native (raw output via ASIO)

- **ASIO Support**
  - Native ASIO output for professional audio devices

## 🖼️ Screenshots

<img src="doc/img/1.png" width="50%"><img src="doc/img/2.png" width="50%">
<img src="doc/img/3.png" width="50%"><img src="doc/img/4.png" width="50%">
<img src="doc/img/5.png" width="50%"><img src="doc/img/6.png" width="50%">
<img src="doc/img/7.png" width="50%"><img src="doc/img/8.png" width="50%">
<img src="doc/img/9.png" width="50%"><img src="doc/img/10.png" width="50%">

## ✍️ Contributing & Building

Issues and Pull Requests are welcome.

### Build from Source

**Prerequisites**

- [.NET 11 SDK](https://dotnet.microsoft.com/)
- Windows 10 19041 or later
- Visual Studio 2026 or later with the WinUI workload

**Steps**

1. Clone the repository:
   ```bash
   git clone https://github.com/Johnwikix/original-sound-hq-player.git
   ```
2. Open `WinUIMusicPlayer.sln` in Visual Studio and restore NuGet packages
3. Press `Ctrl+Shift+B` to build the solution
4. Press `Ctrl+F5` to launch without debugging

## 💖 Dependencies & Credits

### Third-Party Libraries

| Library | Description | License |
| :--- | :--- | :--- |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | System tray icon | MIT |
| [WinUIEx](https://github.com/dotMorten/WinUIEx) | WinUI window extensions | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM framework | MIT |
| [DevWinUI](https://github.com/ghost1372/DevWinUI) | WinUI extension components | MIT |
| [atldotnet](https://github.com/Zeugma440/atldotnet) | Audio format metadata reading | MIT |
| [BASS](https://www.un4seen.com/) / [ManagedBass](https://github.com/ManagedBass/ManagedBass) | Audio playback engine | Non-Commercial |
| [Lyricify.Lyrics.Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) | Lyrics search and parsing | MIT |
| [Isolation](https://github.com/Storyteller-Studios/Isolation) | Shader fluid background | MIT |
| [Microsoft.PinYinConverter](https://github.com/stanzhai/MsPinyinConverter) | Pinyin conversion | MIT |
| [ZLinq](https://github.com/dotnet/ZLinq) | Zero-allocation LINQ | MIT |
| [sqlite-net-pcl](https://github.com/praeclarum/sqlite-net) | SQLite ORM | MIT |
| [Serilog](https://serilog.net/) | Structured logging | Apache-2.0 |
| [Microsoft.Graphics.Win2D](https://github.com/microsoft/Win2D) | 2D graphics rendering | MIT |

### Code References

- [BetterLyrics](https://github.com/jayfunc/BetterLyrics)
- [WindowsMusicPlayer-TheUntamedMusicPlayer](https://github.com/LanZhan-Harmony/WindowsMusicPlayer-TheUntamedMusicPlayer)
- [HyPlayer](https://github.com/HyPlayer/HyPlayer)
- [DevWinUI](https://github.com/ghost1372/DevWinUI)

## 📄 License

This project is licensed under the [MIT License](LICENSE).

## 📬 Contact

- QQ Group: Group 1 `1009034363`, Group 2 `1033738779`
- Email: [dannypan9709@foxmail.com](mailto:dannypan9709@foxmail.com)

## 🗂️ Data Storage

Application data is stored at:

- User data: `%userprofile%\documents\OriginalSoundPlayer`

---

<div align="center">
  <sub>Crafted with ❤ by Sennpai Studio</sub>
</div>