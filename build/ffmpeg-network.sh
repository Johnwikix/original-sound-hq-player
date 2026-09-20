#!/usr/bin/env bash
set -euo pipefail
# MSYS2 UCRT64. Build in a clean worktree; do not distclean the user's existing source/build.
source_dir="${1:?source worktree required}"
build_dir="${2:?build directory required}"
install_dir="${3:?installation directory required}"
export PATH=/ucrt64/bin:/usr/bin
mkdir -p "$build_dir" "$install_dir"
cd "$build_dir"
"$source_dir/configure" \
  --prefix="$install_dir" --target-os=mingw32 --arch=x86_64 \
  --enable-shared --disable-static --disable-programs --disable-doc \
  --enable-network --enable-schannel --disable-autodetect --disable-debug \
  --disable-avdevice --disable-avfilter --disable-swscale --disable-everything \
  --enable-libmp3lame --enable-libvorbis --enable-libopus \
  --pkg-config-flags=--static --extra-ldflags=-static \
  --enable-demuxer=mp3,flac,ogg,wav,aiff,dsf,iff,ape,wv,mov,asf,aac,ac3,eac3 \
  --enable-decoder=mp3,flac,vorbis,opus,alac,wmav1,wmav2,aac,ac3,eac3,ape,wavpack,dst,dsd_lsbf,dsd_msbf,dsd_lsbf_planar,dsd_msbf_planar,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_u8,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be \
  --enable-encoder=pcm_s16le,pcm_s24le,pcm_f32le,flac,libmp3lame,libvorbis,libopus,aac,alac,wmav2 \
  --enable-muxer=wav,flac,ogg,opus,mp3,ipod,asf \
  --enable-parser=mp3,flac,vorbis,opus,aac,ac3 \
  --enable-protocol=file,http,https,tcp,tls,httpproxy
make -j8
make install
