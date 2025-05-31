using CSCore;
using NAudio.Wave.SampleProviders;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Adapter
{
    public class CSCoreToISampleProviderAdapter : ISampleProvider
    {
        private readonly IWaveSource _source;
        private readonly NAudio.Wave.WaveFormat _waveFormat;
        private readonly byte[] _buffer;
        private readonly int _bufferSize;

        public CSCoreToISampleProviderAdapter(IWaveSource source, int bufferSize = 4096)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _bufferSize = bufferSize;
            _buffer = new byte[bufferSize];

            // 转换CSCore WaveFormat到NAudio WaveFormat
            var csWaveFormat = source.WaveFormat;
            _waveFormat = new NAudio.Wave.WaveFormat(
                csWaveFormat.SampleRate,
                csWaveFormat.BitsPerSample,
                csWaveFormat.Channels);
        }

        public NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            // 计算需要读取的字节数
            int bytesPerSample = _waveFormat.BitsPerSample / 8;
            int bytesToRead = Math.Min(count * bytesPerSample, _buffer.Length);

            // 从CSCore源读取字节数据
            int bytesRead = _source.Read(_buffer, 0, bytesToRead);
            if (bytesRead == 0)
                return 0;

            // 计算实际读取的样本数
            int samplesRead = bytesRead / bytesPerSample;

            // 根据位深度转换字节到float样本
            switch (_waveFormat.BitsPerSample)
            {
                case 16:
                    ConvertInt16ToFloat(_buffer, buffer, offset, samplesRead);
                    break;
                case 24:
                    ConvertInt24ToFloat(_buffer, buffer, offset, samplesRead);
                    break;
                case 32:
                    ConvertInt32ToFloat(_buffer, buffer, offset, samplesRead);
                    break;
                default:
                    throw new NotSupportedException($"不支持的位深度: {_waveFormat.BitsPerSample}");
            }

            return samplesRead;
        }

        private void ConvertInt16ToFloat(byte[] input, float[] output, int offset, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(input, i * 2);
                output[offset + i] = sample / 32768f;
            }
        }

        private void ConvertInt24ToFloat(byte[] input, float[] output, int offset, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = (input[i * 3] << 8) | (input[i * 3 + 1] << 16) | (input[i * 3 + 2] << 24);
                sample = sample >> 8; // 符号扩展
                output[offset + i] = sample / 8388608f;
            }
        }

        private void ConvertInt32ToFloat(byte[] input, float[] output, int offset, int sampleCount)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int sample = BitConverter.ToInt32(input, i * 4);
                output[offset + i] = sample / 2147483648f;
            }
        }

        public void Dispose()
        {
            _source?.Dispose();
        }
    }
}
