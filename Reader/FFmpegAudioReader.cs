using CSCore;
using CSCore.Ffmpeg;
using NAudio.Wave;
using System;

namespace WinUIMusicPlayer.Reader
{
    public class FFmpegAudioReader : WaveStream
    {
        private IWaveSource ffmpegDecoder;
        private readonly NAudio.Wave.WaveFormat waveFormat;
        public override NAudio.Wave.WaveFormat WaveFormat
        {
            get { return waveFormat; }
        }

        public override long Length
        {
            get
            {
                if (ffmpegDecoder != null)
                    return ffmpegDecoder.Length;
                return 0;
            }
        }

        public override long Position
        {
            get
            {
                if (null != ffmpegDecoder)
                    return ffmpegDecoder.Position;
                return 0;
            }
            set
            {
                if (null != ffmpegDecoder)
                    ffmpegDecoder.Position = value;
            }
        }

        public FFmpegAudioReader(string filename)
        {
            ffmpegDecoder = new FfmpegDecoder(filename);
            if (null != ffmpegDecoder)
            {
                int sampleRate = ffmpegDecoder.WaveFormat.SampleRate;
                int bitsPerSample = ffmpegDecoder.WaveFormat.BitsPerSample;
                int channels = ffmpegDecoder.WaveFormat.Channels;
                this.waveFormat = new NAudio.Wave.WaveFormat(sampleRate, bitsPerSample, channels);
            }
        }

        //fill pcm data to buffer
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (null != ffmpegDecoder)
            {
                byte[] tempBuf = new byte[count];
                count = ffmpegDecoder.Read(tempBuf, 0, tempBuf.Length);
                Buffer.BlockCopy(tempBuf, 0, buffer, 0, count);
                return count;
            }
            else
            {
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (null != ffmpegDecoder)
                ffmpegDecoder.Dispose();
        }
    }
}
