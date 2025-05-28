using CSCore;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Adapter
{
    public class CSCoreToNAudioAdapter : IWaveProvider
    {
        private readonly IWaveSource _source;
        private readonly NAudio.Wave.WaveFormat _waveFormat;

        public CSCoreToNAudioAdapter(IWaveSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            int sampleRate = source.WaveFormat.SampleRate;
            if (source.WaveFormat.SampleRate > 384000 && source.WaveFormat.SampleRate <= 768000 )
            {
                sampleRate = source.WaveFormat.SampleRate/2;
            }
            if (source.WaveFormat.SampleRate > 768000 && source.WaveFormat.SampleRate <= 1536000)
            {
                sampleRate = source.WaveFormat.SampleRate / 4;
            }
            // 转换 CSCore WaveFormat 到 NAudio WaveFormat
            _waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                sampleRate,
                source.WaveFormat.Channels);
        }

        public NAudio.Wave.WaveFormat WaveFormat => _waveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            return _source.Read(buffer, offset, count);
        }
    }
}
