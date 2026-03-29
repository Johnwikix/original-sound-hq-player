using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinUIMusicPlayer.Reader
{
    public static class AudioCoverReader
    {
        private const int MaxCoverBytes = 30 * 1024 * 1024;

        public static byte[] ReadCover(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return Array.Empty<byte>();

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".mp3" or ".flac"))
                return Array.Empty<byte>();

            if (!File.Exists(filePath))
                return Array.Empty<byte>();

            try
            {
                using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                return ext == ".flac" ? ReadFlacCover(fs) : ReadId3v2Cover(fs);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        // ── 公共：安全分配 ────────────────────────────────────────────────────────

        /// <summary>
        /// 结合文件剩余字节与声称长度，取较小值分配，防止损坏文件导致虚假大分配。
        /// </summary>
        private static byte[]? AllocateCoverBuffer(Stream s, int claimedSize)
        {
            if (claimedSize <= 0 || claimedSize > MaxCoverBytes)
                return null;

            // 对可 Seek 流（FileStream 必然可以）用真实剩余长度做上界
            int safeSize = claimedSize;
            if (s.CanSeek)
            {
                long streamRemaining = s.Length - s.Position;
                if (streamRemaining <= 0) return null;
                // 取两者中较小值，避免按虚假 claimedSize 分配
                safeSize = (int)Math.Min(claimedSize, streamRemaining);
            }

            return new byte[safeSize];
        }

        // ── ID3v2 (MP3) ──────────────────────────────────────────────────────────

        private static byte[] ReadId3v2Cover(Stream s)
        {
            Span<byte> hdr = stackalloc byte[10];
            if (!ReadExact(s, hdr)) return Array.Empty<byte>();

            if (hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3')
                return Array.Empty<byte>();

            byte ver = hdr[3];
            if (ver is < 3 or > 4) return Array.Empty<byte>();

            if ((hdr[5] & 0x80) != 0) return Array.Empty<byte>(); // unsync

            int tagSize = DecodeSynchsafe(hdr[6], hdr[7], hdr[8], hdr[9]);
            long tagEnd = 10 + tagSize;

            Span<byte> frameBuf = stackalloc byte[10];

            while (s.Position + 10 < tagEnd)
            {
                if (!ReadExact(s, frameBuf)) break;
                if (frameBuf[0] == 0) break;

                string frameId = System.Text.Encoding.Latin1.GetString(frameBuf[..4]);
                int frameSize = ver == 4
                    ? DecodeSynchsafe(frameBuf[4], frameBuf[5], frameBuf[6], frameBuf[7])
                    : (frameBuf[4] << 24 | frameBuf[5] << 16 | frameBuf[6] << 8 | frameBuf[7]);

                if (frameId == "APIC")
                    return ReadApicFrame(s, frameSize);

                if (!Skip(s, frameSize)) break;
            }

            return Array.Empty<byte>();
        }

        private static byte[] ReadApicFrame(Stream s, int size)
        {
            if (size <= 0 || size > MaxCoverBytes) return Array.Empty<byte>();

            Span<byte> tmp = stackalloc byte[1];
            int bytesRead = 0;

            // 1. 编码字节
            if (!ReadExact(s, tmp)) return Array.Empty<byte>();
            byte enc = tmp[0];
            bytesRead++;

            // 2. 跳过 MIME（null 终止）
            while (bytesRead < size)
            {
                if (!ReadExact(s, tmp)) return Array.Empty<byte>();
                bytesRead++;
                if (tmp[0] == 0) break;
            }

            // 3. 图片类型
            if (!ReadExact(s, tmp)) return Array.Empty<byte>();
            bytesRead++;

            // 4. 跳过 description
            if (enc is 1 or 2) // UTF-16：双字节 null
            {
                Span<byte> two = stackalloc byte[2];
                while (bytesRead + 2 <= size)
                {
                    if (!ReadExact(s, two)) return Array.Empty<byte>();
                    bytesRead += 2;
                    if (two[0] == 0 && two[1] == 0) break;
                }
            }
            else
            {
                while (bytesRead < size)
                {
                    if (!ReadExact(s, tmp)) return Array.Empty<byte>();
                    bytesRead++;
                    if (tmp[0] == 0) break;
                }
            }

            // 5. 图片数据：安全分配后直接读入
            int imgSize = size - bytesRead;
            var img = AllocateCoverBuffer(s, imgSize);
            if (img is null) return Array.Empty<byte>();

            return ReadExact(s, img) ? img : Array.Empty<byte>();
        }

        // ── FLAC ─────────────────────────────────────────────────────────────────

        private static byte[] ReadFlacCover(Stream s)
        {
            Span<byte> magic = stackalloc byte[4];
            if (!ReadExact(s, magic)) return Array.Empty<byte>();
            if (magic[0] != 0x66 || magic[1] != 0x4C || magic[2] != 0x61 || magic[3] != 0x43)
                return Array.Empty<byte>();

            Span<byte> hdr = stackalloc byte[4];
            while (true)
            {
                if (!ReadExact(s, hdr)) break;

                bool isLast = (hdr[0] & 0x80) != 0;
                int blockType = hdr[0] & 0x7F;
                int blockLen = hdr[1] << 16 | hdr[2] << 8 | hdr[3];

                if (blockType == 6)
                {
                    var result = ReadFlacPictureStream(s, blockLen);
                    if (result.Length > 0) return result;
                }
                else
                {
                    if (!Skip(s, blockLen)) break;
                }

                if (isLast) break;
            }

            return Array.Empty<byte>();
        }

        private static byte[] ReadFlacPictureStream(Stream s, int blockLen)
        {
            int remaining = blockLen;
            Span<byte> buf4 = stackalloc byte[4];

            // picture type
            if (!ReadExact(s, buf4)) return Array.Empty<byte>();
            remaining -= 4;

            // MIME
            if (!ReadExact(s, buf4)) return Array.Empty<byte>();
            remaining -= 4;
            int mimeLen = BE32(buf4);
            if (mimeLen < 0 || mimeLen > remaining) return Array.Empty<byte>();
            if (!Skip(s, mimeLen)) return Array.Empty<byte>();
            remaining -= mimeLen;

            // description
            if (!ReadExact(s, buf4)) return Array.Empty<byte>();
            remaining -= 4;
            int descLen = BE32(buf4);
            if (descLen < 0 || descLen > remaining) return Array.Empty<byte>();
            if (!Skip(s, descLen)) return Array.Empty<byte>();
            remaining -= descLen;

            // width / height / depth / colors
            if (remaining < 16) return Array.Empty<byte>();
            if (!Skip(s, 16)) return Array.Empty<byte>();
            remaining -= 16;

            // image length
            if (remaining < 4) return Array.Empty<byte>();
            if (!ReadExact(s, buf4)) return Array.Empty<byte>();
            remaining -= 4;
            int imgLen = BE32(buf4);

            if (imgLen > remaining) return Array.Empty<byte>();

            // 安全分配：取声称长度与流剩余的较小值
            var img = AllocateCoverBuffer(s, imgLen);
            if (img is null) return Array.Empty<byte>();

            return ReadExact(s, img) ? img : Array.Empty<byte>();
        }

        // ── 辅助 ─────────────────────────────────────────────────────────────────

        private static bool ReadExact(Stream s, Span<byte> buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf[total..]);
                if (n == 0) return false;
                total += n;
            }
            return true;
        }

        private static bool Skip(Stream s, int count)
        {
            if (count <= 0) return true;
            if (s.CanSeek) { s.Seek(count, SeekOrigin.Current); return true; }
            Span<byte> discard = stackalloc byte[Math.Min(count, 4096)];
            int left = count;
            while (left > 0)
            {
                int n = s.Read(discard[..Math.Min(left, discard.Length)]);
                if (n == 0) return false;
                left -= n;
            }
            return true;
        }

        private static int DecodeSynchsafe(byte b0, byte b1, byte b2, byte b3)
            => (b0 & 0x7F) << 21 | (b1 & 0x7F) << 14 | (b2 & 0x7F) << 7 | (b3 & 0x7F);

        private static int BE32(Span<byte> b)
            => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
    }
}
