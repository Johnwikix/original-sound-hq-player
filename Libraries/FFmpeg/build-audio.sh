#!/usr/bin/env bash
# Run in MSYS2 UCRT64: bash build-audio.sh <clean FFmpeg n9.0.1 source> <build dir>
set -euo pipefail
source_dir=$(cd "$1" && pwd)
mkdir -p "$2"
cd "$2"
"$source_dir/configure" \
  --target-os=mingw32 --arch=x86_64 --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-network --disable-autodetect \
  --disable-debug --disable-avdevice --disable-avfilter --disable-swscale \
  --disable-everything --enable-libmp3lame --enable-libvorbis --enable-libopus \
  --pkg-config-flags=--static --extra-ldflags=-static \
  --enable-demuxer=mp3,flac,ogg,wav,aiff,dsf,iff,ape,wv,mov,asf,aac,ac3,eac3 \
  --enable-decoder=mp3,flac,vorbis,opus,alac,wmav1,wmav2,aac,ac3,eac3,ape,wavpack,dst,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_u8,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be \
  --enable-encoder=pcm_s16le,pcm_s24le,pcm_f32le,flac,libmp3lame,libvorbis,libopus,aac,alac,wmav2 \
  --enable-muxer=wav,flac,ogg,opus,mp3,ipod,asf \
  --enable-parser=mp3,flac,vorbis,opus,aac,ac3 --enable-protocol=file
make -j"${NUMBER_OF_PROCESSORS:-4}"
mkdir -p dist
for library in libavcodec/avcodec-63.dll libavformat/avformat-63.dll libavutil/avutil-61.dll libswresample/swresample-7.dll; do
  cp "$library" dist/
done
strip --strip-unneeded dist/*.dll
