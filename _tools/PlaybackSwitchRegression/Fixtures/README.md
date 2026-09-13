`eac3-5.1.m4a` is a generated one-second, 48 kHz E-AC-3 5.1(side) test signal,
not an Atmos/JOC encode. It reproduces the missing E-AC-3 decoder in the bundled
FFmpeg build without including commercial music. Each channel has a distinct tone.

Generation (FFmpeg CLI):

```powershell
ffmpeg -f lavfi -i 'aevalsrc=0.08*sin(2*PI*440*t)|0.08*sin(2*PI*550*t)|0.08*sin(2*PI*660*t)|0.08*sin(2*PI*80*t)|0.08*sin(2*PI*770*t)|0.08*sin(2*PI*880*t):s=48000:d=1:c=5.1(side)' -c:a eac3 -b:a 384k -f mp4 eac3-5.1.m4a
```
