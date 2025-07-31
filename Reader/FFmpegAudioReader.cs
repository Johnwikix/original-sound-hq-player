using CSCore;
using CSCore.Ffmpeg;
using NAudio.Wave;
using System;

namespace WinUIMusicPlayer.Reader
{
    public class FFmpegAudioReader : WaveStream
    {
        private IWaveSource ffmpegDecoder;
        private ISampleSource sampleSource;
        private readonly NAudio.Wave.WaveFormat waveFormat;

        public override NAudio.Wave.WaveFormat WaveFormat
        {
            get { return waveFormat; }
        }

        public override long Length
        {
            get
            {
                if (sampleSource != null)
                    return sampleSource.Length * 4; // 32-bit = 4 bytes per sample
                return 0;
            }
        }

        public override long Position
        {
            get
            {
                if (null != sampleSource)
                    return sampleSource.Position * 4; // 32-bit = 4 bytes per sample
                return 0;
            }
            set
            {
                if (null != sampleSource)
                    sampleSource.Position = value / 4; // Convert bytes to samples
            }
        }

        public FFmpegAudioReader(string filename)
        {
            ffmpegDecoder = new FfmpegDecoder(filename);
            if (null != ffmpegDecoder)
            {
                int originalSampleRate = ffmpegDecoder.WaveFormat.SampleRate;
                int channels = ffmpegDecoder.WaveFormat.Channels;

                // 限制最高采样率不超过384000
                int targetSampleRate = originalSampleRate;
                if (originalSampleRate > 384000)
                {
                    int divisor = 1;
                    while (originalSampleRate / divisor > 384000)
                    {
                        divisor++;
                    }
                    targetSampleRate = originalSampleRate / divisor;

                    if (targetSampleRate != originalSampleRate)
                    {
                        ffmpegDecoder = new CSCore.DSP.DmoResampler(ffmpegDecoder, targetSampleRate)
                        {
                            Quality = 60 // 设置高质量
                        };
                    }
                }
                sampleSource = ffmpegDecoder.ToSampleSource();
                // 创建32-bit float格式的WaveFormat
                this.waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, channels);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (null != sampleSource)
            {
                // 计算要读取的sample数量
                int sampleCount = count / 4; // 32-bit float = 4 bytes per sample

                // 创建float数组来接收数据
                float[] floatBuffer = new float[sampleCount];

                // 从CSCore读取float数据
                int samplesRead = sampleSource.Read(floatBuffer, 0, sampleCount);

                // 将float数据转换为byte数组
                Buffer.BlockCopy(floatBuffer, 0, buffer, offset, samplesRead * 4);

                return samplesRead * 4; // 返回字节数
            }
            else
            {
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (null != sampleSource)
                {
                    sampleSource.Dispose();
                    sampleSource = null;
                }
                if (null != ffmpegDecoder)
                {
                    ffmpegDecoder.Dispose();
                    ffmpegDecoder = null;
                }
            }
            base.Dispose(disposing);
        }
    }   
}
