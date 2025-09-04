using NAudio.Flac;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Reader
{
    public class MultiTypeAudioReader : WaveStream, ISampleProvider
    {
        private WaveStream readerStream;

        private readonly SampleChannel sampleChannel;

        private readonly int destBytesPerSample;

        private readonly int sourceBytesPerSample;

        private readonly long length;

        private readonly object lockObject;

        //
        // 摘要:
        //     File Name
        public string FileName { get; }

        //
        // 摘要:
        //     WaveFormat of this stream
        public override WaveFormat WaveFormat => sampleChannel.WaveFormat;

        //
        // 摘要:
        //     Length of this stream (in bytes)
        public override long Length => length;

        //
        // 摘要:
        //     Position of this stream (in bytes)
        public override long Position
        {
            get
            {
                return SourceToDest(readerStream.Position);
            }
            set
            {
                lock (lockObject)
                {
                    readerStream.Position = DestToSource(value);
                }
            }
        }

        //
        // 摘要:
        //     Gets or Sets the Volume of this AudioFileReader. 1.0f is full volume
        public float Volume
        {
            get
            {
                return sampleChannel.Volume;
            }
            set
            {
                sampleChannel.Volume = value;
            }
        }

        //
        // 摘要:
        //     Initializes a new instance of AudioFileReader
        //
        // 参数:
        //   fileName:
        //     The file to open
        public MultiTypeAudioReader(string fileName)
        {
            lockObject = new object();
            FileName = fileName;
            CreateReaderStream(fileName);
            sourceBytesPerSample = readerStream.WaveFormat.BitsPerSample / 8 * readerStream.WaveFormat.Channels;
            sampleChannel = new SampleChannel(readerStream, forceStereo: false);
            destBytesPerSample = 4 * sampleChannel.WaveFormat.Channels;
            length = SourceToDest(readerStream.Length);
        }

        //
        // 摘要:
        //     Creates the reader stream, supporting all filetypes in the core NAudio library,
        //     and ensuring we are in PCM format
        //
        // 参数:
        //   fileName:
        //     File Name
        private void CreateReaderStream(string fileName)
        {
            try
            {
                if (AppSettings.IsGlobalFFmpegEnabled) {
                    if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        readerStream = new WaveFileReader(fileName);
                        if (readerStream.WaveFormat.Encoding != WaveFormatEncoding.Pcm && readerStream.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                        {
                            readerStream = WaveFormatConversionStream.CreatePcmStream(readerStream);
                            readerStream = new BlockAlignReductionStream(readerStream);
                        }
                    }
                    else
                    {
                        readerStream = new FFmpegAudioReader(fileName);
                    }
                }
                else {
                    if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        readerStream = new WaveFileReader(fileName);
                        if (readerStream.WaveFormat.Encoding != WaveFormatEncoding.Pcm && readerStream.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                        {
                            readerStream = WaveFormatConversionStream.CreatePcmStream(readerStream);
                            readerStream = new BlockAlignReductionStream(readerStream);
                        }
                    }
                    else if (fileName.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                    {
                        readerStream = new FlacReader(fileName);
                    }
                    else if (fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Environment.OSVersion.Version.Major < 6)
                        {
                            readerStream = new Mp3FileReader(fileName);
                        }
                        else
                        {
                            readerStream = new MediaFoundationReader(fileName);
                        }
                    } else if (fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    {
                        readerStream = new VorbisWaveReader(fileName);
                    }
                    else if (fileName.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".aif", StringComparison.OrdinalIgnoreCase))
                    {
                        readerStream = new AiffFileReader(fileName);
                    }
                    else
                    {
                        readerStream = new MediaFoundationReader(fileName);
                    }
                }                
            }
            catch (Exception ex)
            {
                readerStream = new FFmpegAudioReader(fileName);
            }
        }

        //
        // 摘要:
        //     Reads from this wave stream
        //
        // 参数:
        //   buffer:
        //     Audio buffer
        //
        //   offset:
        //     Offset into buffer
        //
        //   count:
        //     Number of bytes required
        //
        // 返回结果:
        //     Number of bytes read
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                WaveBuffer waveBuffer = new WaveBuffer(buffer);
                int count2 = count / 4;
                return Read(waveBuffer.FloatBuffer, offset / 4, count2) * 4;
            }
            catch (AccessViolationException)
            {
                return 0;
            }
            catch (Exception) {
                return 0;
            }           
        }

        //
        // 摘要:
        //     Reads audio from this sample provider
        //
        // 参数:
        //   buffer:
        //     Sample buffer
        //
        //   offset:
        //     Offset into sample buffer
        //
        //   count:
        //     Number of samples required
        //
        // 返回结果:
        //     Number of samples read
        public int Read(float[] buffer, int offset, int count)
        {
            lock (lockObject)
            {
                return sampleChannel.Read(buffer, offset, count);
            }
        }

        //
        // 摘要:
        //     Helper to convert source to dest bytes
        private long SourceToDest(long sourceBytes)
        {
            return destBytesPerSample * (sourceBytes / sourceBytesPerSample);
        }

        //
        // 摘要:
        //     Helper to convert dest to source bytes
        private long DestToSource(long destBytes)
        {
            return sourceBytesPerSample * (destBytes / destBytesPerSample);
        }

        //
        // 摘要:
        //     Disposes this AudioFileReader
        //
        // 参数:
        //   disposing:
        //     True if called from Dispose
        protected override void Dispose(bool disposing)
        {
            if (disposing && readerStream != null)
            {
                readerStream.Dispose();
                readerStream = null;
            }

            base.Dispose(disposing);
        }
    }
}
