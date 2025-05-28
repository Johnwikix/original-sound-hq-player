using CSCore;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Adapter
{
    public class CSCoreTo16BitAdapter : IWaveProvider
    {
        private readonly IWaveSource _source;
        private readonly NAudio.Wave.WaveFormat _waveFormat;
        private byte[] _sourceBuffer;

        public CSCoreTo16BitAdapter(IWaveSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            // 创建16位PCM格式
            _waveFormat = new NAudio.Wave.WaveFormat(
                source.WaveFormat.SampleRate,
                16, // 16位
                source.WaveFormat.Channels);

            // 源缓冲区用于读取32位浮点数据
            _sourceBuffer = new byte[8192]; // 可根据需要调整大小
        }

        public NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            // 计算需要多少32位浮点数据来填充16位PCM缓冲区
            int floatBytesNeeded = count * 2; // 32位浮点是16位PCM的2倍大小

            // 确保源缓冲区足够大
            if (_sourceBuffer.Length < floatBytesNeeded)
            {
                Array.Resize(ref _sourceBuffer, floatBytesNeeded);
            }

            // 从源读取32位浮点数据
            int bytesRead = _source.Read(_sourceBuffer, 0, floatBytesNeeded);

            // 转换32位浮点到16位PCM
            int samplesRead = bytesRead / 4; // 每个32位浮点样本占4字节
            int outputBytes = 0;

            for (int i = 0; i < samplesRead && outputBytes < count; i++)
            {
                // 读取32位浮点值
                float sample = BitConverter.ToSingle(_sourceBuffer, i * 4);

                // 限制到[-1.0, 1.0]范围
                sample = Math.Max(-1.0f, Math.Min(1.0f, sample));

                // 转换为16位PCM
                short pcmSample = (short)(sample * short.MaxValue);

                // 写入输出缓冲区
                if (outputBytes + 1 < count)
                {
                    byte[] pcmBytes = BitConverter.GetBytes(pcmSample);
                    buffer[offset + outputBytes] = pcmBytes[0];
                    buffer[offset + outputBytes + 1] = pcmBytes[1];
                    outputBytes += 2;
                }
            }

            return outputBytes;
        }
    }
}
