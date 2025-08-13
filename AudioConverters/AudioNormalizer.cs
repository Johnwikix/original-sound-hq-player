using CSCore;
using System;

namespace WinUIMusicPlayer.AudioConverters
{
    public class AudioNormalizer : ISampleSource
    {
        private readonly ISampleSource _source;
        private readonly float _targetLevel;
        private float _peakLevel = 0f;
        private readonly object _lock = new object();

        public AudioNormalizer(ISampleSource source, float targetLevel = 0.95f)
        {
            _source = source;
            _targetLevel = targetLevel;
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
                // 找到峰值
                float currentPeak = 0f;
                for (int i = offset; i < offset + samplesRead; i++)
                {
                    float abs = Math.Abs(buffer[i]);
                    if (abs > currentPeak)
                        currentPeak = abs;
                }

                lock (_lock)
                {
                    if (currentPeak > _peakLevel)
                        _peakLevel = currentPeak;
                }

                // 如果峰值超过目标电平，进行标准化
                if (_peakLevel > _targetLevel)
                {
                    float gain = _targetLevel / _peakLevel;
                    for (int i = offset; i < offset + samplesRead; i++)
                    {
                        buffer[i] *= gain;
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
