using CSCore;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.AudioAdapter
{
    public class CSCoreToNAudioAdapter : IWaveProvider
    {
        private readonly IWaveSource csCoreWaveSource;

        public CSCoreToNAudioAdapter(IWaveSource csCoreWaveSource)
        {
            this.csCoreWaveSource = csCoreWaveSource;
            WaveFormat = new NAudio.Wave.WaveFormat(
                csCoreWaveSource.WaveFormat.SampleRate,
                csCoreWaveSource.WaveFormat.BitsPerSample,
                csCoreWaveSource.WaveFormat.Channels
            );
        }

        public NAudio.Wave.WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            return csCoreWaveSource.Read(buffer, offset, count);
        }
    }
}
