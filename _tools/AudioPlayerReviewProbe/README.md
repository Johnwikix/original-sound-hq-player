# AudioPlayer review probes

Run from the repository root:

```powershell
dotnet run --project _tools/AudioPlayerReviewProbe
```

Diagnostic observations for the 2026-09-11 review, not a pass/fail regression suite.
Uses the shipping Session/Decoder/IPC envelope and a controlled native WASAPI
vtable. Does not open a physical audio device, start the IPC server, or acquire
the application's named mutexes. Requires the repository's `_tools/test_tone.wav`
fixture and bundled FFmpeg DLLs.

The fake Initialize operation remains blocked across the three-second timeout.
Stop/Reset record whether cleanup calls occur before Initialize is released.
The fake native vtable is retained until this short-lived probe process exits.

`results.txt` contains the observed output. See
`../../docs/audioplayer-review-2026-09-11.md` for findings and limitations.
