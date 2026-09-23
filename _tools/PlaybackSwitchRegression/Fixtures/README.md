`eac3-5.1.m4a` is a generated one-second, 48 kHz E-AC-3 5.1(side) test signal,
not an Atmos/JOC encode. It reproduces the missing E-AC-3 decoder in the bundled
FFmpeg build without including commercial music. Each channel has a distinct tone.

Generation (FFmpeg CLI):

```powershell
ffmpeg -f lavfi -i 'aevalsrc=0.08*sin(2*PI*440*t)|0.08*sin(2*PI*550*t)|0.08*sin(2*PI*660*t)|0.08*sin(2*PI*80*t)|0.08*sin(2*PI*770*t)|0.08*sin(2*PI*880*t):s=48000:d=1:c=5.1(side)' -c:a eac3 -b:a 384k -f mp4 eac3-5.1.m4a
```

`flac-invalid-tail.flac` contains a generated six-second, 44.1 kHz stereo tone
(264,600 valid PCM frames), followed by 64 bytes with values 0 through 63. This
reproduces `AVERROR_INVALIDDATA` after the complete audio has decoded. It guards
natural EOF and seeking after EOF, including DirectSound with fading enabled.

Generate the clean file, then append the bytes in Python:

```powershell
ffmpeg -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=6' -ac 2 -c:a flac -compression_level 5 -metadata_header_padding 0 -fflags +bitexact -flags:a +bitexact tone.flac
python -c "from pathlib import Path; Path('flac-invalid-tail.flac').write_bytes(Path('tone.flac').read_bytes() + bytes(range(64)))"
```
