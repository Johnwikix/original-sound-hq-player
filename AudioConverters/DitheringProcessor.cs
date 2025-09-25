//using CSCore;
//using System;

//namespace WinUIMusicPlayer.AudioConverters
//{
//    public class DitheringProcessor : ISampleSource
//    {
//        private readonly ISampleSource _source;
//        private readonly int _targetBits;
//        private readonly Random _random;
//        private readonly float _ditherAmount;

//        public DitheringProcessor(ISampleSource source, int targetBits = 24)
//        {
//            _source = source;
//            _targetBits = targetBits;
//            _random = new Random();
//            // 计算抖动量 - 约为最低有效位的1/2
//            _ditherAmount = 1.0f / (float)(1 << (_targetBits - 1)) * 0.5f;
//        }

//        public WaveFormat WaveFormat => _source.WaveFormat;
//        public bool CanSeek => _source.CanSeek;
//        public long Length => _source.Length;
//        public long Position
//        {
//            get => _source.Position;
//            set => _source.Position = value;
//        }

//        public int Read(float[] buffer, int offset, int count)
//        {
//            int samplesRead = _source.Read(buffer, offset, count);

//            if (samplesRead > 0)
//            {
//                for (int i = offset; i < offset + samplesRead; i++)
//                {
//                    // 添加三角分布抖动噪声
//                    float dither = (_random.NextSingle() - 0.5f) * _ditherAmount;
//                    buffer[i] += dither;

//                    // 量化到目标位深度
//                    float quantized = QuantizeToTargetBits(buffer[i], _targetBits);
//                    buffer[i] = quantized;
//                }
//            }

//            return samplesRead;
//        }

//        private float QuantizeToTargetBits(float sample, int bits)
//        {
//            // 将样本量化到指定位深度
//            float maxValue = (1 << (bits - 1)) - 1;
//            float quantized = Math.Max(-1.0f, Math.Min(1.0f, sample));
//            quantized = (float)Math.Round(quantized * maxValue) / maxValue;
//            return quantized;
//        }

//        public void Dispose()
//        {
//            _source?.Dispose();
//        }
//    }
//}
