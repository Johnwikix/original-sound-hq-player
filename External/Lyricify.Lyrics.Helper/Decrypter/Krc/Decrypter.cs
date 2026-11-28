using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using System.Text;

namespace Lyricify.Lyrics.Decrypter.Krc
{
    public class Decrypter
    {
        protected readonly static byte[] DecryptKey = { 0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69 };

        /// <summary>
        /// 解密 KRC 歌词
        /// </summary>
        /// <param name="encryptedLyrics">加密的歌词</param>
        /// <returns>解密后的 KRC 歌词</returns>
        public static string? DecryptLyrics(string encryptedLyrics) => DecryptLyrics(encryptedLyrics, int.MaxValue);

        public static string? DecryptLyrics(string encryptedLyrics, int maxDecompressedBytes)
        {
            var data = Convert.FromBase64String(encryptedLyrics)[4..];

            for (var i = 0; i < data.Length; ++i)
            {
                data[i] = (byte)(data[i] ^ DecryptKey[i % DecryptKey.Length]);
            }

            var res = Encoding.UTF8.GetString(SharpZipLibDecompress(data, maxDecompressedBytes));
            return res[1..];
        }

        private static byte[] SharpZipLibDecompress(byte[] data, int maxDecompressedBytes)
        {
            using var compressed = new MemoryStream(data);
            using var decompressed = new MemoryStream();
            using var inputStream = new InflaterInputStream(compressed);
            var buffer = new byte[8192];
            int read;
            while ((read = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decompressed.Length + read > maxDecompressedBytes)
                    throw new InvalidDataException("KRC decompression exceeds the lyric size limit.");
                decompressed.Write(buffer, 0, read);
            }
            return decompressed.ToArray();
        }
    }
}
