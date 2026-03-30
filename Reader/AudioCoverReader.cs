using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

public static class AudioCoverReader
{
    private const int MaxCoverBytes = 30 * 1024 * 1024;

    public static byte[] ReadCover(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return Array.Empty<byte>();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);

            return ext switch
            {
                ".mp3" => ReadId3v2Cover(fs),
                //".aiff" or ".aif" => ReadAiffCover(fs),
                ".wav" => ReadRiffCover(fs),
                ".flac" => ReadFlacCover(fs),
                //".ogg" or ".oga" or ".opus" => ReadOggCover(fs),
                ".m4a" => ReadMp4Cover(fs),
                //".ape" => ReadApeCover(fs),
                _ => Array.Empty<byte>()
            };
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    // ── ID3v2 核心解析（MP3 / AIFF / WAV 共用）────────────────────────────────

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
        long tagEnd = s.Position + tagSize; // Position 已在读完 10 字节后

        Span<byte> frameBuf = stackalloc byte[10];
        while (s.Position + 10 < tagEnd)
        {
            if (!ReadExact(s, frameBuf)) break;
            if (frameBuf[0] == 0) break;

            string frameId = Encoding.Latin1.GetString(frameBuf[..4]);
            int frameSize = ver == 4
                ? DecodeSynchsafe(frameBuf[4], frameBuf[5], frameBuf[6], frameBuf[7])
                : BE32(frameBuf[4..]);

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
        int consumed = 0;

        if (!ReadExact(s, tmp)) return Array.Empty<byte>();
        byte enc = tmp[0]; consumed++;

        // 跳过 MIME（Latin-1 null 终止）
        while (consumed < size)
        {
            if (!ReadExact(s, tmp)) return Array.Empty<byte>();
            consumed++;
            if (tmp[0] == 0) break;
        }

        // 图片类型
        if (!ReadExact(s, tmp)) return Array.Empty<byte>();
        consumed++;

        // 跳过 description
        if (enc is 1 or 2)
        {
            Span<byte> two = stackalloc byte[2];
            while (consumed + 2 <= size)
            {
                if (!ReadExact(s, two)) return Array.Empty<byte>();
                consumed += 2;
                if (two[0] == 0 && two[1] == 0) break;
            }
        }
        else
        {
            while (consumed < size)
            {
                if (!ReadExact(s, tmp)) return Array.Empty<byte>();
                consumed++;
                if (tmp[0] == 0) break;
            }
        }

        int imgSize = size - consumed;
        var img = AllocateCoverBuffer(s, imgSize);
        if (img is null) return Array.Empty<byte>();
        return ReadExact(s, img) ? img : Array.Empty<byte>();
    }

    // ── AIFF / AIF（IFF 大端容器）────────────────────────────────────────────
    // 结构：FORM [4] + 总长 [4BE] + "AIFF" [4] + chunks...
    // chunk：ID [4] + size [4BE] + data；奇数 size 需补 1 字节对齐

    private static byte[] ReadAiffCover(Stream s)
    {
        Span<byte> buf = stackalloc byte[12];
        if (!ReadExact(s, buf)) return Array.Empty<byte>();

        // "FORM" + size + "AIFF"
        if (!MatchFourCC(buf, 0, "FORM")) return Array.Empty<byte>();
        if (!MatchFourCC(buf, 8, "AIFF") && !MatchFourCC(buf, 8, "AIFC"))
            return Array.Empty<byte>();

        Span<byte> chunkHdr = stackalloc byte[8];
        while (ReadExact(s, chunkHdr))
        {
            string id = Encoding.Latin1.GetString(chunkHdr[..4]);
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(chunkHdr[4..]);

            if (id == "ID3 " || id == "id3 ")
                return ReadId3v2Cover(s); // 流当前位置即 ID3v2 头

            if (!Skip(s, size + (size & 1))) break; // IFF 偶数对齐
        }

        return Array.Empty<byte>();
    }

    // ── WAV（RIFF 小端容器）──────────────────────────────────────────────────
    // 结构：RIFF [4] + 总长 [4LE] + "WAVE" [4] + chunks...

    private static byte[] ReadRiffCover(Stream s)
    {
        Span<byte> buf = stackalloc byte[12];
        if (!ReadExact(s, buf)) return Array.Empty<byte>();

        if (!MatchFourCC(buf, 0, "RIFF") || !MatchFourCC(buf, 8, "WAVE"))
            return Array.Empty<byte>();

        Span<byte> chunkHdr = stackalloc byte[8];
        while (ReadExact(s, chunkHdr))
        {
            string id = Encoding.Latin1.GetString(chunkHdr[..4]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..]);

            if (id is "id3 " or "ID3 " or "id3\0")
                return ReadId3v2Cover(s);

            if (!Skip(s, size + (size & 1))) break; // RIFF 偶数对齐
        }

        return Array.Empty<byte>();
    }

    // ── FLAC（原生 metadata blocks）──────────────────────────────────────────

    private static byte[] ReadFlacCover(Stream s)
    {
        Span<byte> magic = stackalloc byte[4];
        if (!ReadExact(s, magic)) return Array.Empty<byte>();
        if (magic[0] != 0x66 || magic[1] != 0x4C || magic[2] != 0x61 || magic[3] != 0x43)
            return Array.Empty<byte>();

        Span<byte> hdr = stackalloc byte[4];
        while (ReadExact(s, hdr))
        {
            bool isLast = (hdr[0] & 0x80) != 0;
            int type = hdr[0] & 0x7F;
            int blockLen = hdr[1] << 16 | hdr[2] << 8 | hdr[3];

            if (type == 6)
            {
                var r = ReadFlacPictureStream(s, blockLen);
                if (r.Length > 0) return r;
            }
            else if (!Skip(s, blockLen)) break;

            if (isLast) break;
        }

        return Array.Empty<byte>();
    }

    private static byte[] ReadFlacPictureStream(Stream s, int blockLen)
    {
        int left = blockLen;
        Span<byte> b4 = stackalloc byte[4];

        if (!ReadExact(s, b4) || (left -= 4) < 0) return Array.Empty<byte>(); // type

        if (!ReadExact(s, b4) || (left -= 4) < 0) return Array.Empty<byte>();
        int mimeLen = BE32(b4);
        if (mimeLen < 0 || mimeLen > left || !Skip(s, mimeLen)) return Array.Empty<byte>();
        left -= mimeLen;

        if (!ReadExact(s, b4) || (left -= 4) < 0) return Array.Empty<byte>();
        int descLen = BE32(b4);
        if (descLen < 0 || descLen > left || !Skip(s, descLen)) return Array.Empty<byte>();
        left -= descLen;

        if (left < 20 || !Skip(s, 16)) return Array.Empty<byte>(); // w/h/depth/colors
        left -= 16;

        if (!ReadExact(s, b4) || (left -= 4) < 0) return Array.Empty<byte>();
        int imgLen = BE32(b4);
        if (imgLen > left) return Array.Empty<byte>();

        var img = AllocateCoverBuffer(s, imgLen);
        if (img is null) return Array.Empty<byte>();
        return ReadExact(s, img) ? img : Array.Empty<byte>();
    }

    // ── Ogg（.ogg / .oga / .opus）────────────────────────────────────────────
    // Ogg 页结构 → Vorbis/Opus comment packet → METADATA_BLOCK_PICTURE（base64）
    // 只读前几个 Ogg 页（comment packet 必然在开头）

    private static byte[] ReadOggCover(Stream s)
    {
        // 读取并拼接 comment packet 的原始字节（跨页则继续拼）
        byte[]? packet = ReadFirstOggCommentPacket(s);
        if (packet is null) return Array.Empty<byte>();
        return ParseVorbisCommentPacket(packet);
    }

    private static byte[]? ReadFirstOggCommentPacket(Stream s)
    {
        // Ogg 页头固定部分：capture(4) + version(1) + type(1) + granule(8)
        //   + serial(4) + seqno(4) + crc(4) + nsegs(1) = 27 bytes
        // 然后是 nsegs 个 lacing 字节，再是数据

        using var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[27];
        bool foundComment = false;
        int pageCount = 0;

        while (pageCount++ < 8) // comment packet 必在前几页，超出即放弃
        {
            if (!ReadExact(s, hdr)) return null;
            if (hdr[0] != 'O' || hdr[1] != 'g' || hdr[2] != 'g' || hdr[3] != 'S')
                return null;

            byte headerType = hdr[5];
            byte nsegs = hdr[26];

            Span<byte> segtab = stackalloc byte[nsegs];
            if (!ReadExact(s, segtab)) return null;

            // 计算本页数据总长
            int pageDataLen = 0;
            foreach (byte b in segtab) pageDataLen += b;

            // 第 1 页（序号 0）是 ident packet，跳过；
            // 第 2 页开始是 comment packet（type byte = 3 for Vorbis, 分析内容）
            if (pageCount == 1 && (headerType & 0x02) != 0) // BOS
            {
                // 跳过 ident 页
                if (!Skip(s, pageDataLen)) return null;
                continue;
            }

            // 读取页数据
            var pageData = new byte[pageDataLen];
            if (!ReadExact(s, pageData)) return null;
            ms.Write(pageData);

            // 检查是否为 comment packet（Vorbis: 0x03+"vorbis"；Opus: "OpusTags"）
            if (!foundComment)
            {
                var buf = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                if (buf.Length >= 7 &&
                    (IsVorbisComment(buf) || IsOpusComment(buf)))
                {
                    foundComment = true;
                }
            }

            // 最后一个 lacing 字节 < 255 表示 packet 结束
            if (foundComment && segtab[nsegs - 1] < 255)
                return ms.ToArray();

            if (!foundComment && ms.Length > 128 * 1024)
                return null; // comment 不可能这么大还没找到
        }

        return null;
    }

    private static bool IsVorbisComment(Span<byte> buf)
        => buf.Length >= 7 && buf[0] == 0x03 &&
           buf[1] == 'v' && buf[2] == 'o' && buf[3] == 'r' &&
           buf[4] == 'b' && buf[5] == 'i' && buf[6] == 's';

    private static bool IsOpusComment(Span<byte> buf)
        => buf.Length >= 8 &&
           buf[0] == 'O' && buf[1] == 'p' && buf[2] == 'u' && buf[3] == 's' &&
           buf[4] == 'T' && buf[5] == 'a' && buf[6] == 'g' && buf[7] == 's';

    private static byte[] ParseVorbisCommentPacket(byte[] packet)
    {
        // Vorbis comment packet:
        //   1+6 bytes type/magic (Vorbis) 或 8 bytes (Opus)
        //   4LE  vendor string length + vendor string
        //   4LE  comment count
        //   repeated: 4LE length + "KEY=VALUE"

        int pos = IsVorbisComment(packet) ? 7 : 8;

        // vendor string
        if (pos + 4 > packet.Length) return Array.Empty<byte>();
        int vendorLen = LE32(packet, pos); pos += 4;
        pos += vendorLen;

        // comment count
        if (pos + 4 > packet.Length) return Array.Empty<byte>();
        int count = LE32(packet, pos); pos += 4;

        for (int i = 0; i < count; i++)
        {
            if (pos + 4 > packet.Length) break;
            int len = LE32(packet, pos); pos += 4;
            if (len < 0 || pos + len > packet.Length) break;

            // 只取 key 部分做比较，避免分配整个字符串
            ReadOnlySpan<byte> entry = packet.AsSpan(pos, len);
            pos += len;

            // METADATA_BLOCK_PICTURE=<base64>
            const string prefix = "METADATA_BLOCK_PICTURE=";
            if (len <= prefix.Length) continue;
            if (!StartsWithAsciiIgnoreCase(entry, prefix)) continue;

            // base64 解码
            string b64 = Encoding.ASCII.GetString(entry[prefix.Length..]);
            byte[] raw;
            try { raw = Convert.FromBase64String(b64); }
            catch { continue; }

            // raw 就是 FLAC PICTURE block 的二进制内容，复用现有解析
            using var ms = new MemoryStream(raw);
            return ReadFlacPictureStream(ms, raw.Length);
        }

        return Array.Empty<byte>();
    }

    // ── MP4 / M4A（ISOBMFF box 树）───────────────────────────────────────────
    // 路径：moov → udta → meta → ilst → covr → data

    private static readonly string[] CoverBoxPath =
        ["moov", "udta", "meta", "ilst", "covr"];

    private static byte[] ReadMp4Cover(Stream s)
    {
        // 递归沿路径下钻，最后在 covr box 里找 data box
        long fileLen = s.Length;
        if (!DescendBoxPath(s, 0, fileLen, CoverBoxPath, 0))
            return Array.Empty<byte>();

        // 现在 s 在 covr box 内容起始处，covr 直接包含 data box
        return ReadMp4DataBox(s);
    }

    /// <summary>
    /// 沿 path[depth..] 在 [start, start+len) 范围内递归查找，
    /// 找到最后一级时停在其内容起始处返回 true。
    /// </summary>
    private static bool DescendBoxPath(
        Stream s, long start, long containerLen,
        string[] path, int depth)
    {
        long end = start + containerLen;
        s.Seek(start, SeekOrigin.Begin);

        // meta box 有 4 字节 version+flags 要跳过
        bool isMeta = depth > 0 && path[depth - 1] == "meta";
        if (isMeta) { if (!Skip(s, 4)) return false; }

        Span<byte> hdr = stackalloc byte[8];
        while (s.Position + 8 <= end)
        {
            if (!ReadExact(s, hdr)) return false;
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string boxType = Encoding.Latin1.GetString(hdr[4..8]);

            long dataStart = s.Position;
            long dataLen = boxSize - 8;

            if (boxSize == 1) // 64-bit extended size
            {
                Span<byte> ext = stackalloc byte[8];
                if (!ReadExact(s, ext)) return false;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                dataStart = s.Position;
                dataLen = boxSize - 16;
            }

            if (boxSize == 0) // box 延伸到文件末尾
                dataLen = end - dataStart;

            if (dataLen < 0) return false;

            if (boxType == path[depth])
            {
                if (depth == path.Length - 1)
                {
                    // 到达目标层，流位置已在内容起始
                    s.Seek(dataStart, SeekOrigin.Begin);
                    return true;
                }
                // 继续下钻
                if (DescendBoxPath(s, dataStart, dataLen, path, depth + 1))
                    return true;
            }

            // 跳到下一个 box
            if (boxSize == 0) return false;
            s.Seek(dataStart + dataLen, SeekOrigin.Begin);
        }

        return false;
    }

    private static byte[] ReadMp4DataBox(Stream s)
    {
        // covr 下是一个或多个 data box：
        // size(4BE) + "data"(4) + type_indicator(4) + locale(4) + image bytes
        Span<byte> hdr = stackalloc byte[8];
        if (!ReadExact(s, hdr)) return Array.Empty<byte>();

        long boxSize = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        if (!MatchFourCC(hdr, 4, "data")) return Array.Empty<byte>();

        // type_indicator + locale = 8 bytes
        if (!Skip(s, 8)) return Array.Empty<byte>();

        int imgLen = (int)(boxSize - 8 - 8); // box - header - type/locale
        if (imgLen <= 0) return Array.Empty<byte>();

        var img = AllocateCoverBuffer(s, imgLen);
        if (img is null) return Array.Empty<byte>();
        return ReadExact(s, img) ? img : Array.Empty<byte>();
    }

    // ── APE（APEv2 tag，通常在文件尾）────────────────────────────────────────
    // APEv2 footer：preamble(8) + version(4LE) + size(4LE) + count(4LE) + flags(4) + reserved(8)
    // tag items：size(4LE) + flags(4LE) + key(null-term ASCII) + value

    private static byte[] ReadApeCover(Stream s)
    {
        if (s.Length < 32) return Array.Empty<byte>();

        // 从文件尾往前找 APEv2 footer（32 bytes）
        s.Seek(-32, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[32];
        if (!ReadExact(s, footer)) return Array.Empty<byte>();

        // preamble = "APETAGEX"
        if (!MatchFourCC(footer, 0, "APET") || !MatchFourCC(footer, 4, "AGEX"))
            return Array.Empty<byte>();

        int version = LE32b(footer, 8);
        if (version != 2000) return Array.Empty<byte>(); // 只处理 APEv2

        int tagSize = LE32b(footer, 12); // 包含 footer，不含 header
        int itemCount = LE32b(footer, 16);

        // tag 起始位置（footer 前 tagSize 字节）
        long tagStart = s.Length - tagSize;
        if (tagStart < 0) return Array.Empty<byte>();
        s.Seek(tagStart, SeekOrigin.Begin);

        // 如果有 header（flags bit 31），跳过 32 bytes
        int footerFlags = LE32b(footer, 20);
        bool hasHeader = (footerFlags & (1 << 29)) != 0; // bit 29 = has header
        if (hasHeader && !Skip(s, 32)) return Array.Empty<byte>();

        // 遍历 items
        Span<byte> itemHdr = stackalloc byte[8];
        for (int i = 0; i < itemCount; i++)
        {
            if (!ReadExact(s, itemHdr)) break;
            int valueSize = LE32b(itemHdr, 0);
            // int itemFlags = LE32b(itemHdr, 4); // 0=text,1=binary,2=external

            // 读 key（null 终止 ASCII，通常很短）
            using var keyBuf = new MemoryStream(32);
            Span<byte> oneByte = stackalloc byte[1];
            while (true)
            {
                if (!ReadExact(s, oneByte)) return Array.Empty<byte>();
                if (oneByte[0] == 0) break;
                keyBuf.WriteByte(oneByte[0]);
                if (keyBuf.Length > 255) return Array.Empty<byte>(); // 异常保护
            }

            string key = Encoding.Latin1.GetString(keyBuf.GetBuffer(), 0, (int)keyBuf.Length);

            if (string.Equals(key, "Cover Art (Front)", StringComparison.OrdinalIgnoreCase))
            {
                // APEv2 封面 value 格式：null-terminated filename + 图片数据
                // 先跳过 filename（到第一个 null）
                int skipped = 0;
                while (skipped < valueSize)
                {
                    if (!ReadExact(s, oneByte)) return Array.Empty<byte>();
                    skipped++;
                    if (oneByte[0] == 0) break;
                }

                int imgLen = valueSize - skipped;
                var img = AllocateCoverBuffer(s, imgLen);
                if (img is null) return Array.Empty<byte>();
                return ReadExact(s, img) ? img : Array.Empty<byte>();
            }

            if (!Skip(s, valueSize)) break;
        }

        return Array.Empty<byte>();
    }

    // ── 公共辅助 ──────────────────────────────────────────────────────────────

    private static byte[]? AllocateCoverBuffer(Stream s, int claimedSize)
    {
        if (claimedSize <= 0 || claimedSize > MaxCoverBytes) return null;
        int safeSize = claimedSize;
        if (s.CanSeek)
        {
            long remaining = s.Length - s.Position;
            if (remaining <= 0) return null;
            safeSize = (int)Math.Min(claimedSize, remaining);
        }
        return new byte[safeSize];
    }

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

    private static bool Skip(Stream s, long count)
    {
        if (count <= 0) return true;
        if (s.CanSeek) { s.Seek(count, SeekOrigin.Current); return true; }
        Span<byte> discard = stackalloc byte[4096];
        long left = count;
        while (left > 0)
        {
            int n = s.Read(discard[..(int)Math.Min(left, discard.Length)]);
            if (n == 0) return false;
            left -= n;
        }
        return true;
    }

    private static bool MatchFourCC(Span<byte> buf, int offset, string fourCC)
        => buf[offset] == fourCC[0] && buf[offset + 1] == fourCC[1] &&
           buf[offset + 2] == fourCC[2] && buf[offset + 3] == fourCC[3];

    private static int DecodeSynchsafe(byte b0, byte b1, byte b2, byte b3)
        => (b0 & 0x7F) << 21 | (b1 & 0x7F) << 14 | (b2 & 0x7F) << 7 | (b3 & 0x7F);

    private static int BE32(Span<byte> b) =>
        b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];

    private static int LE32(byte[] b, int offset) =>
        b[offset] | b[offset + 1] << 8 | b[offset + 2] << 16 | b[offset + 3] << 24;

    private static int LE32b(Span<byte> b, int offset) =>
        b[offset] | b[offset + 1] << 8 | b[offset + 2] << 16 | b[offset + 3] << 24;

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> data, string prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            byte b = data[i];
            char c = prefix[i];
            if (b != c && (b | 0x20) != (c | 0x20)) return false;
        }
        return true;
    }
}