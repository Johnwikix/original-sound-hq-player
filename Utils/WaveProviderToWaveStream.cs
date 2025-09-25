//using NAudio.Wave;

//namespace WinUIMusicPlayer.Utils
//{
//    public class WaveProviderToWaveStream : WaveStream
//    {
//        private readonly IWaveProvider provider;
//        private readonly AudioFileReader reader;
//        private long position;

//        public WaveProviderToWaveStream(IWaveProvider provider, AudioFileReader reader)
//        {
//            this.provider = provider;
//            this.reader = reader;
//        }

//        public override WaveFormat WaveFormat => provider.WaveFormat;

//        public override long Length => reader.Length;

//        public override long Position
//        {
//            get => position;
//            set => position = value;
//        }

//        public override int Read(byte[] buffer, int offset, int count)
//        {
//            int bytesRead = reader.Read(buffer, offset, count);
//            position += bytesRead;
//            return bytesRead;
//        }
//    }
//}
