using CSCore;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Adapter
{
    public class LightweightCSCoreAdapter : WaveStream
    {
        private readonly IWaveSource _source;
        private readonly NAudio.Wave.WaveFormat _waveFormat;
        private readonly bool _ownsSource;

        // 缓存关键属性，避免重复计算
        private readonly long _cachedLength;
        private long _position;

        // 避免重复的Position设置
        private long _lastSetPosition = -1;

        public LightweightCSCoreAdapter(IWaveSource source, bool ownsSource = false)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            _source = source;
            _ownsSource = ownsSource;

            // 处理超高采样率转换（修复语法）
            if (source.WaveFormat.SampleRate > 384000)
            {
                int originalSampleRate = source.WaveFormat.SampleRate;
                int targetSampleRate = originalSampleRate > 768000 ?
                    originalSampleRate / 4 : originalSampleRate / 2;

                var resampled = source.ToSampleSource()
                    .ChangeSampleRate(targetSampleRate)
                    .ToWaveSource();

                if (_ownsSource) _source.Dispose();
                _source = resampled;
            }

            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                _source.WaveFormat.SampleRate,
                _source.WaveFormat.Channels);

            // 一次性获取并缓存长度，避免重复文件访问
            try
            {
                _cachedLength = _source.Length;
            }
            catch
            {
                _cachedLength = 0;
            }
        }

        public override NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        // 返回缓存的长度，避免每次都访问文件
        public override long Length => _cachedLength;

        public override long Position
        {
            get => _position;
            set
            {
                value = Math.Max(0, Math.Min(value, Length));

                // 避免重复设置相同位置
                if (value == _lastSetPosition)
                {
                    _position = value;
                    return;
                }

                if (_source.CanSeek)
                {
                    _source.Position = value;
                    _position = value;
                    _lastSetPosition = value;
                }
                else
                {
                    throw new NotSupportedException("底层音频源不支持定位");
                }
            }
        }

        // 提供直接的时间设置方法，避免 WaveChannel32.CurrentTime 的开销
        public void SetCurrentTime(TimeSpan time)
        {
            long targetPosition = (long)(time.TotalSeconds * _waveFormat.AverageBytesPerSecond);

            // 确保位置对齐到样本边界
            int bytesPerSample = (_waveFormat.BitsPerSample / 8) * _waveFormat.Channels;
            targetPosition = (targetPosition / bytesPerSample) * bytesPerSample;

            Position = targetPosition;
        }

        // 获取当前时间，避免通过 WaveChannel32
        public TimeSpan GetCurrentTime()
        {
            return TimeSpan.FromSeconds((double)Position / _waveFormat.AverageBytesPerSecond);
        }

        public TimeSpan GetTotalTime()
        {
            return TimeSpan.FromSeconds((double)Length / _waveFormat.AverageBytesPerSecond);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 确保请求的字节数是样本的整数倍
            int bytesPerSample = (_waveFormat.BitsPerSample / 8) * _waveFormat.Channels;
            count = (count / bytesPerSample) * bytesPerSample;

            int bytesRead = _source.Read(buffer, offset, count);
            _position += bytesRead;

            return bytesRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsSource)
            {
                _source?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
