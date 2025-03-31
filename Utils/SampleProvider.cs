using NAudio.Wave;
using NVorbis;

namespace WinUIMusicPlayer.Utils
{
    public class SampleProvider : ISampleProvider
    {
        private readonly VorbisReader _reader;
        public WaveFormat WaveFormat { get; }

        public SampleProvider(VorbisReader reader, WaveFormat format)
        {
            _reader = reader;
            WaveFormat = format;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return _reader.ReadSamples(buffer, offset, count);
        }
    }
}
