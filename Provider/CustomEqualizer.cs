//using NAudio.Dsp;
//using NAudio.Wave;

//namespace WinUIMusicPlayer.Provider
//{
//    public class CustomEqualizer : ISampleProvider
//    {
//        private readonly ISampleProvider sourceProvider;
//        private readonly CustomEqualizerBand[] bands;
//        private readonly BiQuadFilter[,] filters;
//        private readonly int channels;
//        private readonly int bandCount;
//        private bool updated;

//        /// <summary>
//        /// Creates a new Equalizer
//        /// </summary>
//        public CustomEqualizer(ISampleProvider sourceProvider, CustomEqualizerBand[] bands)
//        {
//            this.sourceProvider = sourceProvider;
//            this.bands = bands;
//            channels = sourceProvider.WaveFormat.Channels;
//            bandCount = bands.Length;
//            filters = new BiQuadFilter[channels, bands.Length];
//            CreateFilters();
//        }

//        private void CreateFilters()
//        {
//            for (int bandIndex = 0; bandIndex < bandCount; bandIndex++)
//            {
//                var band = bands[bandIndex];
//                for (int n = 0; n < channels; n++)
//                {
//                    if (filters[n, bandIndex] is null)
//                        filters[n, bandIndex] = BiQuadFilter.PeakingEQ(sourceProvider.WaveFormat.SampleRate, band.Frequency, band.Bandwidth, band.Gain);
//                    else
//                        filters[n, bandIndex].SetPeakingEq(sourceProvider.WaveFormat.SampleRate, band.Frequency, band.Bandwidth, band.Gain);
//                }
//            }
//        }

//        /// <summary>
//        /// Update the equalizer settings
//        /// </summary>
//        public void Update()
//        {
//            updated = true;
//            CreateFilters();
//        }

//        /// <summary>
//        /// Gets the WaveFormat of this Sample Provider
//        /// </summary>
//        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

//        /// <summary>
//        /// Reads samples from this Sample Provider
//        /// </summary>
//        public int Read(float[] buffer, int offset, int count)
//        {
//            int samplesRead = sourceProvider.Read(buffer, offset, count);

//            if (updated)
//            {
//                CreateFilters();
//                updated = false;
//            }

//            for (int n = 0; n < samplesRead; n++)
//            {
//                int ch = n % channels;

//                for (int band = 0; band < bandCount; band++)
//                {
//                    buffer[offset + n] = filters[ch, band].Transform(buffer[offset + n]);
//                }
//            }
//            return samplesRead;
//        }
//    }
//}
