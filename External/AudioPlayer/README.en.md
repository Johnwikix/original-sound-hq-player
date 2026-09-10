**[中文](README.md)** | English

# AudioPlayer — Standalone Audio Playback Process

The playback engine of the OriginalSound HQ Player — a self-contained
NativeAOT single-file process decoupled from the UI. FFmpeg decoding plus a
self-developed WASAPI/ASIO interop layer, communicating with the main app over
the IPC contract in `External\BassPlayerIpc.Shared` (byte-compatible envelope
and serialization; named objects `AudioPlayer_SharedMemory` /
`AudioPlayer_RequestReady` / `AudioPlayer_ResponseReady` /
`AudioPlayer_NotificationReady` / `AudioPlayer_SingleInstanceMutex`, plus the
client liveness mutex `WinUIMusicPlayer_SingleInstanceMutex`).

Originally built on top of the bass family (bass / basswasapi / bassasio /
bassdsd / bass_fx), those have been removed entirely. The architecture still
mirrors the bass IPC surface one-to-one (see the mapping table at the end) so
behavior can be cross-checked against the legacy implementation.

## Architecture

```
AudioPlayer.exe (NativeAOT single file, win-x64)
├── Program.cs                Entry: SustainedLowLatency + timeBeginPeriod(1) + IPC service
├── PlayerIpcService.cs       MMF + semaphore IPC surface (request/response/notification)
├── Decode/
│   ├── PcmDecoder.cs         FFmpeg decode → swresample → float64 interleaved
│   │                         (unified path for PCM and DSD→PCM; DSD output rate = DSD/8,
│   │                         resampleable to DsdPcmFreq)
│   ├── DsdRawReader.cs       DSDIFF/DSF demuxing and WV-DSD reader entry point
│   └── WavPackDsdReader.cs   libwavpack native DSD decompression and 64-bit seek
├── Playback/
│   ├── PlaybackEngine.cs     Engine: session lifecycle, output mode, watchdog recovery,
│   │                         exclusive track-switch reuse
│   ├── Session.cs            Playback session: decode thread + ring buffer + EQ/gain
│   │                         (IRenderSource)
│   ├── Ring.cs               SPSC frame rings (session epoch guards against seek cross-talk;
│   │                         PcmRing/DopRing/DsdByteRing)
│   ├── Dsp.cs                10-band peaking EQ (RBJ, full double) + sample-accurate gain ramp
│   └── RenderSource.cs       Render interface and output mode definitions
├── Interop/
│   ├── WasapiInterop.cs      Raw COM vtable WASAPI / device enumeration / endpoint notifications
│   ├── WasapiOutput.cs       Shared (AUTOCONVERTPCM pass-through) and exclusive Push/Event
│   ├── AsioInterop.cs        IASIO raw vtable + SDK structures
│   ├── AsioHost.cs           ASIO host: buffer candidates / sample-rate pivoting / hidden
│   │                         message window / native DSD
│   └── Win32.cs              Kernel objects / registry / window P/Invokes
└── Diagnostics/BufferDump.cs AP_DUMP env-var-gated endpoint byte dump (64MB cap)
```

## Audio Pipeline

**Full float64** end to end: decode (swr `AV_SAMPLE_FMT_DBL`) → `PcmRing`
(double interleaved) → EQ/volume/fade-in-out (double domain) → per-device
format conversion at the exit (float32 / 16/24/32-bit integer, DoP, DSD
bitstream). Processing noise floor below -140 dBFS; 24-bit sources remain
bit-transparent across the entire chain.

- **PCM**: FFmpeg decode with sample-accurate position (anchor + frames played)
- **DoP**: `DsdRawReader` raw bitstream → uint32 samples; 0x05/0xFA markers
  alternate according to a **global render frame counter** (phase does not
  flip at odd buffer boundaries); silence payload 0x6969
- **Native DSD**: ASIO future extension (kAsioSetIoFormat 0x23111961 magic;
  vendor-private return values like FiiO's 0x3F4847A0 judged by SDK semantics);
  LSB1/MSB1/NER8 paths fully implemented; sample-rate domain auto-tries both
  bit-rate and byte-rate

**EQ**: RBJ peaking filter (bandwidth 1.0 octave, centers 32 Hz–16 kHz);
coefficients and filter state all in double; recomputed on parameter change and
atomically swapped via snapshot (render thread is lock-free read-only).
Bitstream sessions (DoP/NativeDSD) refuse EQ (EqState rolls back).
**Gain ramp**: sample-accurate linear 300 ms (anti-zipper for volume changes
plus fade-in/out); in WasapiShared the steady-state gain returns to 1 (volume
is carried by session volume, not stacked).

## Output Modes

| Mode string | Implementation |
|---|---|
| `DirectSound` / `WasapiShared` | WASAPI shared, **AUTOCONVERTPCM pass-through of source format**: rate/channel conversion delegated to the audio engine — changing the system "Output audio format" no longer requires session rebuild |
| `WasapiExclusivePush` / `WasapiExclusiveEvent` | Format candidate negotiation (PCM: float32→24in32→16→32; DoP: 24packed→24in32→32), `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` alignment retry, MMCSS Pro Audio render thread, Initialize 3-second timeout graveyard semantics |
| `ASIO` | Registry-enumerated drivers, STA message-window thread (prevents driver self-deadlock), buffer candidate enumeration, sample-rate pivoting (with pivot attempts), channel-type validation |

**Endpoint following** (shared mode): listens for default-device switches,
endpoint disable/unplug, and system format changes — silently swaps the
output during playback (decode ring and position preserved); while paused,
the old output is discarded and the new device is acquired on resume.

**Buffer policy**: DirectSound / WASAPI shared use the supplied `Latency` in
milliseconds for both source-format and mix-format initialization (nonpositive
values request 1ms); the audio engine determines the actual frame count. ASIO
reads the driver's preferred size after PCM/DSD and sample-rate negotiation on
each initialization, tries it first, and logs any fallback to a compatible size.
`Latency` still affects decode-ring capacity, but does not select ASIO device
buffers. Automatic rebuilding after live driver-panel buffer changes is not yet
wired up; see the [recovery investigation](ASIO-buffer-recovery.md) (Chinese).

**Exclusive track-switch reuse** (ASIO and WASAPI exclusive): PCM switches with
the same device, output mode, sample rate, and channel count only replace the
render source. Rate, channel, bitstream format, or device changes stop and dispose
the old output before negotiating a new driver format and buffers. New sessions
select PCM / DoP / Native DSD from the new file and current settings, independently
of the previous session's format or fallback result.

**Watchdog auto-recovery**: output failures trigger an auto-rebuild plan at
+1s/+3s/+7s (preserving position); user operations immediately invalidate
any pending plan; two consecutive <4s failures suspend auto-recovery (prevents
error-loop spam); the UI no longer flickers on transient failures.

**Failure fallback**: exclusive/ASIO failure → fall back to WASAPI shared
(session forces PCM with position-preserving rebuild); ASIO native DSD
negotiation failure → first degrade to ASIO DoP, then to shared PCM.

## Build

Prerequisite: .NET 11 SDK (pinned to the 11.0 RC1 series by the repo's
`global.json`).

```
cd External\AudioPlayer
dotnet publish -c Release -p:Platform=x64
# Output: bin\x64\Release\net11.0\win-x64\publish\AudioPlayer.exe (single-file AOT)
```

Runtime dependencies: FFmpeg DLLs in the same directory as the exe
(`avcodec-63 / avformat-63 / avutil-61 / swresample-7`). FFmpeg.AutoGen
locates them via the exe's directory by default — no path adaptation needed.

WV-DSD bitstream playback also uses `wavpackdll.dll` (official 5.9.0 x64, 247.5 KiB,
BSD-3-Clause). AudioPlayer build/publish copies it and `Licenses/WavPack.txt` automatically.
See `Libraries/WavPack/BUILD_INFO.txt` for provenance and the binary hash.
WavPack content identifies DSD; regular PCM WV stays on the FFmpeg path. With bitstream
enabled, `OPEN_DSD_NATIVE` feeds ASIO Native DSD (with ASIO DoP fallback), or WASAPI
exclusive Push/Event DoP. Shared output or disabled bitstream uses FFmpeg DSD-to-PCM.

## Deployment Layout

Flat application root: the main app exe, `AudioPlayer.exe`, and a single set
of FFmpeg DLLs all sit side-by-side (one copy shared by both processes).

- `Player\AudioPlayer.exe` in the repo is the staged publish artifact
  (`-o Player` overwrites it on publish)
- The main project csproj maps `Player\*.exe` and `Player\*.dll` plus
  `Libraries\FFmpeg\x64\*.dll` to the output root via
  `<Link>%(Filename)%(Extension)</Link>` — building deploys.
- `Libraries/WavPack/x64/wavpackdll.dll` goes to the application root and its BSD
  notice goes to `Licenses/WavPack.txt`. Any duplicate DLL staged by `-o Player`
  is excluded from the main app's wildcard to avoid duplicate packaging.

## Verification Status

The WV-DSD extension has device-free regression coverage for byte-exact decompression,
Native DSD/DoP session payloads, seek, odd EOF padding and output-mode selection.
WV-DSD audible playback still requires verification on a real DAC.

Full smoke chain (`AudioPlayerSmokeTest`) + endpoint byte-dump math
verification (`_tools\analysis`) is green:

- Sine rms / Goertzel / zero-crossing match theoretical values
- EQ-on measured gain agrees with the RBJ-double transfer function to 4
  decimal places
- DoP markers alternate strictly throughout, payload byte-identical to the
  source `.dff`
- ASIO Int32LSB and Native DSD (MSB1) bitstreams are byte-exact

Real-hardware (FiiO KA13): ASIO / exclusive DoP / native DSD audible output
and post-format-change auto-recovery are both verified.

## Tools (`_tools\`)

| Tool | Purpose |
|---|---|
| `AudioPlayerSmokeTest` | IPC smoke client: `dotnet run -- <exe> <audio> [sec] [--mode=mode] [--dop] [--dev=N] [--vol=F] [--no-toggle] [--no-eq] [--devices-first] [--trackchange]` |
| `PlaybackSwitchRegression` | Device-free regressions using the real sessions, decoders, and output interop: PCM rates, PCM↔DSD, reuse boundaries, and concurrent WASAPI release. From the repository root: `dotnet run --project _tools/PlaybackSwitchRegression`; see the tool README |
| `analysis/*.py` | Dump verifiers: `gen_sine.py` (standard sine source), `verify_sine.py` (Goertzel + rms + zero-crossing), `verify_dop.py` (DoP marker phase + payload diff), `verify_dsd.py` (DSD bitstream diff) |
| `AsioProbe` | Driver-level IASIO probe (channel types / DSD extension / sample-rate domain) |
| `RawWasapiProbe` | WASAPI shared format probe (which formats the sound card accepts) |
| `WasapiPushProbe` | Exclusive Push-mode behavior probe |
| `FormatSwitch` | System output format read/write (validates post-format-change recovery) |

Dump switch: set `AP_DUMP=<path>` to enable endpoint byte dumps
(`AP_DUMP_MAX` for byte cap).

## References

- [ECHO](https://github.com/Moekotori/ECHO/) — reference implementation for
  WASAPI exclusive negotiation, DoP packing and marker-phase normalization,
  ASIO sample-rate pivoting / buffer candidates / native DSD extension, and
  pre-buffered ring buffer semantics.
- Function mapping vs. the legacy bass-based implementation (historical
  cross-reference):

| bass component | This implementation |
|---|---|
| bass.dll decode + format plugins | `Decode\PcmDecoder` (FFmpeg, unified float64 pipeline) |
| bassdsd (DSD→PCM) | FFmpeg DSD decoder + swr to DsdPcmFreq + double-domain DsdGain |
| bassdsd (DoP) | `Decode\DsdRawReader` + `Playback\Ring.DopRing` (global frame counter markers) |
| bassasio DSD Native | ASIO DSD extension (`ASIOFuture kAsioSetIoFormat`, LSB1/MSB1/NER8) |
| basswasapi shared | WASAPI shared + AUTOCONVERTPCM pass-through + session volume + endpoint following |
| basswasapi exclusive Push/Event | `Interop\WasapiOutput`: candidate negotiation / alignment retry / init-timeout graveyard |
| bassasio | `Interop\AsioInterop/AsioHost`: raw vtable + buffer candidates + sample-rate pivoting + message window |
| bass_fx PeakEQ | `Playback\Dsp.Equalizer`: RBJ double, lock-free atomic-snapshot rendering |
| DirectSound output | Maps to the shared pass-through path (DirectSound is itself a WASAPI-shared wrapper) |
