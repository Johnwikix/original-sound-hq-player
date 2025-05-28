using CSCore;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Adapter
{
    public class CSCoreToWaveStreamAdapter : WaveStream
    {
        private readonly IWaveSource _source;
        private readonly NAudio.Wave.WaveFormat _waveFormat; // 明确使用 NAudio 的 WaveFormat
        private long _position;
        private readonly bool _ownsSource;

        public CSCoreToWaveStreamAdapter(IWaveSource source, bool ownsSource = false)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            _source = source;
            _ownsSource = ownsSource;

            // 处理超高采样率
            int sampleRate = source.WaveFormat.SampleRate; // 使用 CSCore 的 WaveFormat
            if (sampleRate > 384000 && sampleRate <= 768000)
            {
                sampleRate = sampleRate / 2;
                _source = _source.ChangeSampleRate(sampleRate);
            }
            else if (sampleRate > 768000 && sampleRate <= 1536000)
            {
                sampleRate = sampleRate / 4;
                _source = _source.ChangeSampleRate(sampleRate);
            }

            // 转换 CSCore WaveFormat 到 NAudio WaveFormat
            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                _source.WaveFormat.SampleRate, // 使用 CSCore 的 WaveFormat
                _source.WaveFormat.Channels);  // 使用 CSCore 的 WaveFormat
        }

        public override NAudio.Wave.WaveFormat WaveFormat => _waveFormat; // 明确返回类型

        public override long Length
        {
            get
            {
                if (_source.Length == 0)
                    return 0;

                // 计算总长度（以字节为单位）
                return _source.Length;
            }
        }

        public override long Position
        {
            get => _position;
            set
            {
                // 确保位置在有效范围内
                value = Math.Max(0, Math.Min(value, Length));

                // 计算样本位置
                long samplePosition = value / (_waveFormat.BitsPerSample / 8 * _waveFormat.Channels);

                // 设置 CSCore 源的位置
                if (_source.CanSeek)
                {
                    // 计算样本数
                    long sampleCount = value / (_waveFormat.BitsPerSample / 8 * _waveFormat.Channels);

                    // 计算对应的时间跨度
                    double totalSeconds = (double)sampleCount / _waveFormat.SampleRate;
                    TimeSpan timePosition = TimeSpan.FromSeconds(totalSeconds);

                    // 设置 CSCore 源的位置
                    _source.SetPosition(timePosition);
                    _position = value;
                }
                else
                {
                    throw new NotSupportedException("底层音频源不支持定位");
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // 确保请求的字节数是样本的整数倍
            int bytesPerSample = (_waveFormat.BitsPerSample / 8) * _waveFormat.Channels;
            count = (count / bytesPerSample) * bytesPerSample;

            // 从 CSCore 源读取数据
            int bytesRead = _source.Read(buffer, offset, count);

            // 更新位置
            _position += bytesRead;

            return bytesRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsSource)
            {
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
