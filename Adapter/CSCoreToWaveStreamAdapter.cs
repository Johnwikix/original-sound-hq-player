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
        private readonly NAudio.Wave.WaveFormat _waveFormat;
        private long _position;
        private readonly bool _ownsSource;

        public CSCoreToWaveStreamAdapter(IWaveSource source, bool ownsSource = false)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            _source = source;
            _ownsSource = ownsSource;

            // 只在超高采样率下进行转换，并缓存结果
            int originalSampleRate = source.WaveFormat.SampleRate;
            if (originalSampleRate > 384000)
            {
                int targetSampleRate;
                if (originalSampleRate <= 768000)
                    targetSampleRate = originalSampleRate / 2;
                else
                    targetSampleRate = originalSampleRate / 4;
                _source = _source.ToSampleSource().ChangeSampleRate(targetSampleRate).ToWaveSource();
            }

            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                _source.WaveFormat.SampleRate,
                _source.WaveFormat.Channels);
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

                if (_source.CanSeek)
                {
                    // 直接设置字节位置（避免时间转换）
                    _source.Position = value;
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
