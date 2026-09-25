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
    <img src="https://img.shields.io/badge/License-AGPL--3.0-blue" alt="License">
    <a href="https://github.com/Johnwikix/original-sound-hq-player/stargazers"><img src="https://img.shields.io/github/stars/Johnwikix/original-sound-hq-player" alt="Star"></a>
    <a href="https://github.com/Johnwikix/original-sound-hq-player/releases/latest"><img src="https://img.shields.io/github/downloads/Johnwikix/original-sound-hq-player/total?label=Downloads" alt="Downloads"></a>
  </div>

  <br>

</div>

<br>

<div align="center">

[**🏠 Product Page/User Guide**](https://johnwikix.github.io/original-sound-player-page) | [**🐞 Report Issue**](https://github.com/Johnwikix/original-sound-hq-player/issues)

</div>

<br>

## 📥 Download & Install

<div align="center">

| Microsoft Store (Recommended) |
| :---: |
| <a href="https://apps.microsoft.com/detail/9NFW1RPPT999?referrer=appbadge&mode=direct"><img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/></a><br>Get the best installation and update experience from the Microsoft Store |

</div>

## 🔒 Free Trial & Full Version

The app is becoming paid starting with v1.2.3.0 (Microsoft Store channel): installing starts a free trial with every feature available (trial length as shown on the Store listing). After the trial ends the player keeps playing your local music — core playback, the 10-band equalizer, and the remaining DSP effects (loudness normalization, unified preamp, channel & headphone effects) stay free, while the following full-version features are locked:

- **Convolution correction**: curve editor, WAV impulse response (IR) import, per-output-device binding, and preset management
- **DSD bitstream output**: DoP and DSD Native
- **5.1 multichannel PCM output**
- **Atmos (E-AC-3/JOC) HDMI bitstream passthrough** (requires a compatible receiver)

While restricted, your existing settings are never modified or erased — complete the purchase from the DSP/general-settings banner or the About page and your original settings take effect again automatically. Builds obtained outside the Microsoft Store (including compiling from source) are not subject to these restrictions.

## 🌟 Features

- 🎵 **Music Library Browsing**
  - Multi-dimensional browsing by song, artist, album, folder, or favorites
  - Manual local folder scanning with automatic rescan to keep your library in sync
  - Quick access to your favorite tracks

- ☁️ **WebDAV Music Sources**
  - Connect to WebDAV servers (NAS, Nutstore, Alist/OpenList, etc.) and manage them alongside your local library
  - Browse the remote directory tree to pick scan roots; incremental rescans with optional scan-on-startup
  - Automatic remote metadata (title/artist/album, etc.) and embedded cover art, plus matching `.lrc` lyrics by filename
  - HTTP Range streaming playback with automatic stream-recovery; live offline marking and availability probing per source
  - Optional audio cache: fully cached tracks play offline, with a size limit and one-tap cleanup
  - HTTPS self-signed certificate confirmation by fingerprint; filter browsing by source (all/local/individual WebDAV source)

- 📂 **Favorites & Playlists**
  - Create and manage custom playlists
  - A dedicated "Favorite Audio List" for quick access

- 🎛️ **Full Playback Control**
  - Play, pause, skip, shuffle, sequential, single-loop
  - Customizable global shortcuts

- 🔊 **Professional Audio Processing**
  - Supports 12+ audio formats including DSD, FLAC, WAV, MP3
  - Audio conversion: WAV, FLAC, ALAC, MP3, AAC, OGG, OPUS with selectable lossy bitrate
  - Built-in 10-band equalizer (per-band gain and Q) with multiple presets
  - Convolution correction: draw a correction curve (2–32 draggable control points with named presets that can be updated or deleted in place) or import a headphone/room-correction WAV impulse response (IR); rendered into a minimum-phase FIR for real-time convolution, resampled to the output rate automatically; correction curves can be bound per output device and are applied automatically when devices are plugged, removed, or switched; edits take effect instantly with auto-save, and offline editing is supported when no audio output is active; live combined EQ/convolution response preview in the editor
  - Unified preamp: applied to EQ and convolution together — set manually or auto-compensated from the combined response peak (1 dB headroom, normalized to −1 dB) to prevent clipping from correction boosts
  - Loudness normalization: background EBU R128 analysis per track, smooth fixed gain applied once complete and cached — files untouched, dynamics uncompressed (target −24 to −12 LUFS, default −18)
  - Channel & headphone effects: balance, L/R swap, mono mixdown, headphone crossfeed, stereo width
  - One-tap DSP master switch bypasses all effects; DoP / Native DSD bitstream playback bypasses them automatically with settings preserved
  - Full float64 high-precision audio pipeline with bit-transparent handling of 24-bit sources

- 🌈 **Shader Backgrounds & Word-by-word Lyrics**
  - 7 dynamic shader backgrounds (Fluid, PS3 XMB, Rotating Mesh, Liquid Flow, Gradient Flow, Wavy, Chromatic Resonance) rendered live with cover-based color extraction, plus light-wave and fog ambience effects
  - Now-playing word-by-word lyrics: per-word highlighting, character float/scale animation, and eased per-line scrolling (FlowWave spring / per-line stagger easing options) with customizable font size, colors and opacity
  - Standalone desktop lyrics window: word/line modes, dual-line display (original + translation), glow and outline, fog/snow/rain effects, custom colors, lockable and click-through

- 📝 **Music Info & Lyrics**
  - Real-time display of title, artist, album, duration, sample rate, bitrate, file type
  - Automatic album art and lyrics matching

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

- **WASAPI Exclusive Mode**
  - Push/event driven modes that bypass system mixing for lower latency and interference
  - Automatic format negotiation with buffer alignment retry
  - Same-format track switches hand over seamlessly with zero device interaction

- **WASAPI Shared / DirectSound Mode**
  - Source-format pass-through; sample rate and channel conversion handled by the audio engine
  - Follows default device switches, output format changes, and hot-unplug — output switches silently during playback

- **DSD Output**
  - DSD DoP (encapsulated into PCM frames in exclusive mode)
  - DSD Native (raw bitstream via ASIO)

- **ASIO Support**
  - Native ASIO output with automatic driver enumeration and buffer/rate negotiation
  - ASIO native DSD extension support (LSB1/MSB1/NER8)

- **Reliability**
  - Watchdog auto-recovery on output failure (playback position preserved) — device hotplug or format changes won't interrupt listening
  - End-of-track waits for the device pipeline to fully drain and progress nets out buffered audio — no clipped tails, no premature track switching
  - Device selection uses stable endpoint IDs, so replugged devices never resolve to the wrong output

## 🖼️ Screenshots

<img src="doc/img/en/1.jpg" width="50%"><img src="doc/img/en/2.jpg" width="50%">
<img src="doc/img/en/3.jpg" width="50%"><img src="doc/img/en/4.jpg" width="50%">
<img src="doc/img/en/5.jpg" width="50%"><img src="doc/img/en/6.jpg" width="50%">
<img src="doc/img/en/7.jpg" width="50%"><img src="doc/img/en/8.jpg" width="50%">
<img src="doc/img/en/9.jpg" width="50%"><img src="doc/img/en/10.jpg" width="50%">
<img src="doc/img/en/11.jpg" width="50%"><img src="doc/img/en/12.jpg" width="50%">
<img src="doc/img/en/13.jpg" width="50%"><img src="doc/img/en/14.jpg" width="50%">
<img src="doc/img/en/15.jpg" width="50%"><img src="doc/img/en/16.jpg" width="50%">
<img src="doc/img/en/17.jpg" width="50%"><img src="doc/img/en/18.jpg" width="50%">
<img src="doc/img/en/19.jpg" width="50%"><img src="doc/img/en/20.jpg" width="50%">

## ✍️ Contributing & Building

Issues and Pull Requests are welcome.

### Build from Source

**Prerequisites**

- [.NET 11 SDK](https://dotnet.microsoft.com/) (pinned to the 11.0 RC1 series by `global.json`; prerelease SDKs must be allowed)
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

### Build and Deploy Release from the Command Line (no Visual Studio)

With just the .NET 11 SDK, the dotnet CLI can build and deploy the same way
Visual Studio does when running Release:

```powershell
# 1. Build the Release deployment layout (the TFM segment changes with TargetFramework)
$layout = "bin\x64\Release\net11.0-windows10.0.26100.0\win-x64"
dotnet build WinUIMusicPlayer.csproj -c Release -p:Platform=x64

# 2. Copy Content assets into the layout (app icons/tiles, default covers, and UpdateNotes.json
#    are not copied to the build output by default; VS does this automatically when deploying)
Copy-Item Assets\*.png, Assets\icon.ico "$layout\Assets\" -Force
Copy-Item UpdateNotes.json $layout -Force

# 3. Register the build output with the system — the same deployment step VS performs
#    (requires Developer Mode in Windows settings)
Add-AppxPackage -Register "$layout\AppxManifest.xml"
```

- The registration points at the build output folder: rebuild (repeating steps 2–3) and relaunch to run the new build; re-register after the manifest version changes
- The app shows up in the Start menu; remove the registration with `Get-AppxPackage SennpaiStudio.528762A6196EF | Remove-AppxPackage`
- Add `-ForceApplicationShutdown` when re-registering while the app is running
- For a distributable installer (.msix), add `-p:GenerateAppxPackageOnBuild=true` to step 1; the package is written to the repository's `AppPackages\` folder (sign it yourself before installing)

> **Architecture note**: audio playback runs in a standalone process,
> `External\AudioPlayer` (FFmpeg + self-developed WASAPI/ASIO interop, published
> as a single-file NativeAOT binary; `Player\AudioPlayer.exe` in the repo is the
> staged build artifact), communicating with the main app over shared-memory
> IPC. See [External/AudioPlayer/README.md](External/AudioPlayer/README.md) for
> engine details.

## 💰 Donations

If you find OriginalSound HI-FI Player helpful, consider buying the developer a coffee ☕ — your support keeps the project alive and updated!

**[Afdian](https://afdian.com/a/SennpaiStudio)** · Alipay / WeChat Pay QR codes:

| Alipay | WeChat Pay |
| :---: | :---: |
| <img src="doc/img/donation/alipay.jpg" width="240" alt="Alipay QR code"> | <img src="doc/img/donation/wechat.png" width="240" alt="WeChat Pay QR code"> |

## 💖 Dependencies & Credits

### Third-Party Libraries

| Library | Description | License |
| :--- | :--- | :--- |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | System tray icon | MIT |
| [WinUIEx](https://github.com/dotMorten/WinUIEx) | WinUI window extensions | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM framework | MIT |
| [DevWinUI](https://github.com/ghost1372/DevWinUI) | WinUI extension components | MIT |
| [atldotnet (z440.atl.core)](https://github.com/Zeugma440/atldotnet) | Audio format metadata reading | MIT |
| [FFmpeg](https://ffmpeg.org/) (9.0.1 minimal audio build) | Audio decoding/resampling/conversion (one shared DLL set for both processes) | LGPL-2.1+ |
| [FFmpeg.AutoGen](https://github.com/FFmpeg/FFmpeg.AutoGen) | .NET bindings for FFmpeg | MIT |
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

This project is licensed under the [GNU AGPL-3.0 License](LICENSE).

## 📬 Contact

- QQ Group: Group 1 `1009034363`, Group 2 `1033738779`
- Email: [dannypan9709@foxmail.com](mailto:dannypan9709@foxmail.com)

## 🗂️ Data Storage

Application data is stored at:

- User data: `%userprofile%\documents\OriginalSoundPlayer`
- Loudness analysis cache: `%LOCALAPPDATA%\WinUIMusicPlayer\LoudnessCache` (redirected by MSIX filesystem virtualization to the same folder under `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\` when installed from the Store)

---

<div align="center">
  <sub>Crafted with ❤ by Sennpai Studio</sub>
</div>