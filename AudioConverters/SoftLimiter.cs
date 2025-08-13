using CSCore;
using System;

namespace WinUIMusicPlayer.AudioConverters
{
    public class SoftLimiter : ISampleSource
    {
        private readonly ISampleSource _source;
        private readonly float _threshold;
        private readonly float _ratio;

        public SoftLimiter(ISampleSource source, float thresholdDb = -0.1f, float ratio = 10.0f)
        {
            _source = source;
            _threshold = (float)Math.Pow(10, thresholdDb / 20); // 转换为线性值
            _ratio = ratio;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;
        public bool CanSeek => _source.CanSeek;
        public long Length => _source.Length;
        public long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            if (samplesRead > 0)
            {
                for (int i = offset; i < offset + samplesRead; i++)
                {
                    float sample = buffer[i];
                    float absSample = Math.Abs(sample);

                    if (absSample > _threshold)
                    {
                        // 软限制公式
                        float excess = absSample - _threshold;
                        float compressedExcess = excess / _ratio;
                        float newLevel = _threshold + compressedExcess;

                        // 保持符号
                        buffer[i] = sample >= 0 ? newLevel : -newLevel;
                    }
                }
            }

            return samplesRead;
        }

        public void Dispose()
        {
            _source?.Dispose();
        }
    }
}
