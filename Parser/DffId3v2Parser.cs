using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Parser
{
    public class DffId3v2Parser
    {
        // ID3v2 标签的起始标识符 (内部)
        private const string Id3v2Identifier = "ID3";
        // DSDIFF 中包含元数据的 Chunk ID (外部)
        private const string DsdiffId3ChunkId = "ID3 ";

        // 核心工具函数：同步安全整数解码 (ID3v2 内部使用)
        private static int DecodeSynchsafeInteger(byte[] bytes)
        {
            if (bytes.Length != 4) throw new ArgumentException("Synchsafe Integer 必须是 4 字节");
            int size = (bytes[0] << 21) | (bytes[1] << 14) | (bytes[2] << 7) | bytes[3];
            return size;
        }

        // 普通整数解码（ID3v2.3 及之前版本使用）
        private static int DecodeInteger(byte[] bytes)
        {
            if (bytes.Length != 4) throw new ArgumentException("Integer 必须是 4 字节");
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        // 解析图片帧 (APIC)
        private static Id3v2Picture DecodeApicFrame(byte[] frameData, byte majorVersion)
        {
            if (frameData.Length < 5) return null;

            int offset = 0;

            // 读取文本编码
            byte textEncoding = frameData[offset++];
            Encoding encoding = GetEncodingFromByte(textEncoding);

            // 读取 MIME 类型
            string mimeType;
            if (majorVersion >= 3)
            {
                // ID3v2.3+ 使用 MIME 类型字符串
                int nullIndex = Array.IndexOf(frameData, (byte)0, offset);
                if (nullIndex == -1) return null;

                mimeType = Encoding.ASCII.GetString(frameData, offset, nullIndex - offset);
                offset = nullIndex + 1;
            }
            else
            {
                // ID3v2.2 使用 3 字符的图像格式
                if (offset + 3 > frameData.Length) return null;
                string imageFormat = Encoding.ASCII.GetString(frameData, offset, 3);
                mimeType = imageFormat.ToLower() switch
                {
                    "png" => "image/png",
                    "jpg" => "image/jpeg",
                    "jpeg" => "image/jpeg",
                    _ => $"image/{imageFormat.ToLower()}"
                };
                offset += 3;
            }

            // 读取图片类型
            if (offset >= frameData.Length) return null;
            byte pictureType = frameData[offset++];

            // 读取描述
            string description = "";
            if (textEncoding == 1) // UTF-16
            {
                // UTF-16 字符串以双字节 null 结尾
                int nullIndex = -1;
                for (int i = offset; i < frameData.Length - 1; i += 2)
                {
                    if (frameData[i] == 0 && frameData[i + 1] == 0)
                    {
                        nullIndex = i;
                        break;
                    }
                }

                if (nullIndex != -1)
                {
                    description = encoding.GetString(frameData, offset, nullIndex - offset);
                    offset = nullIndex + 2; // 跳过双字节 null
                }
                else
                {
                    offset = frameData.Length; // 没有找到结尾，跳到末尾
                }
            }
            else
            {
                // 单字节编码
                int nullIndex = Array.IndexOf(frameData, (byte)0, offset);
                if (nullIndex != -1)
                {
                    description = encoding.GetString(frameData, offset, nullIndex - offset);
                    offset = nullIndex + 1;
                }
                else
                {
                    offset = frameData.Length; // 没有找到结尾，跳到末尾
                }
            }

            // 剩余数据就是图片数据
            if (offset >= frameData.Length) return null;

            byte[] imageData = new byte[frameData.Length - offset];
            Array.Copy(frameData, offset, imageData, 0, imageData.Length);

            return new Id3v2Picture
            {
                MimeType = mimeType,
                PictureType = pictureType,
                Description = description.Trim('\0'),
                ImageData = imageData
            };
        }

        // 简化的文本帧解码函数 (ID3v2 内部使用)
        private static string DecodeTextFrame(byte[] frameData)
        {
            if (frameData.Length == 0) return string.Empty;

            // 文本帧的第一个字节是编码方式
            byte encodingByte = frameData[0];
            Encoding encoding = GetEncodingFromByte(encodingByte);

            // 文本内容从第二个字节开始
            string text = encoding.GetString(frameData, 1, frameData.Length - 1).Trim('\0');
            return text;
        }

        private static Encoding GetEncodingFromByte(byte encodingByte)
        {
            Encoding encoding = Encoding.GetEncoding("ISO-8859-1"); // 默认

            switch (encodingByte)
            {
                case 1: encoding = Encoding.Unicode; break; // UTF-16
                case 3: encoding = Encoding.UTF8; break;
            }
            return encoding;
        }
        // --- 核心：在文件区域内搜索 ID3v2 头部 ---
        private static long FindId3v2Header(FileStream stream, long startOffset, long endOffset)
        {
            long searchLength = endOffset - startOffset;
            if (searchLength < 10) return -1;

            // 设置搜索块大小（例如 4MB）
            int bufferSize = (int)Math.Min(searchLength, 4 * 1024 * 1024);
            byte[] buffer = new byte[bufferSize];
            stream.Seek(startOffset, SeekOrigin.Begin);
            int bytesRead = stream.Read(buffer, 0, bufferSize);

            if (bytesRead < 3) return -1;

            // 遍历缓冲区，查找 "ID3" 标识符
            for (int i = 0; i <= bytesRead - 3; i++)
            {
                if (buffer[i] == 'I' && buffer[i + 1] == 'D' && buffer[i + 2] == '3')
                {
                    // 找到了 ID3 头部
                    return startOffset + i;
                }
            }
            return -1; // 未找到
        }

        // 在 DFF 文件中查找 ID3 Chunk
        private static long FindDsdiffId3Chunk(FileStream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            byte[] buffer = new byte[8192]; // 8KB 缓冲区

            while (stream.Position < stream.Length - 8)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead < 8) break;

                // 在缓冲区中搜索 "ID3 " 标识符
                for (int i = 0; i <= bytesRead - 4; i++)
                {
                    if (buffer[i] == 'I' && buffer[i + 1] == 'D' &&
                        buffer[i + 2] == '3' && buffer[i + 3] == ' ')
                    {
                        long chunkPosition = stream.Position - bytesRead + i;
                        stream.Seek(chunkPosition + 4, SeekOrigin.Begin);

                        // 读取 chunk 大小
                        byte[] sizeBytes = new byte[8];
                        if (stream.Read(sizeBytes, 0, 8) == 8)
                        {
                            // DFF 使用大端字节序
                            long chunkSize = ((long)sizeBytes[0] << 56) | ((long)sizeBytes[1] << 48) |
                                           ((long)sizeBytes[2] << 40) | ((long)sizeBytes[3] << 32) |
                                           ((long)sizeBytes[4] << 24) | ((long)sizeBytes[5] << 16) |
                                           ((long)sizeBytes[6] << 8) | sizeBytes[7];

                            Debug.WriteLine($"找到 ID3 Chunk 于位置: {chunkPosition}, 大小: {chunkSize}");
                            return chunkPosition + 12; // 返回 ID3 数据的开始位置
                        }
                    }
                }

                // 回退一些字节以防标识符跨越缓冲区边界
                stream.Seek(Math.Max(0, stream.Position - 8), SeekOrigin.Begin);
            }

            return -1;
        }

        // 解析 ID3v2 帧数据
        private static (Dictionary<string, string> textTags, List<Id3v2Picture> pictures) ParseId3v2Frames(FileStream stream, int tagDataSize, byte majorVersion)
        {
            var textTags = new Dictionary<string, string>();
            var pictures = new List<Id3v2Picture>();
            long startPosition = stream.Position;
            long endPosition = startPosition + tagDataSize;

            while (stream.Position < endPosition - 10) // 至少需要 10 字节的帧头
            {
                long frameStartPosition = stream.Position;

                // 读取帧头
                byte[] frameHeader = new byte[10];
                if (stream.Read(frameHeader, 0, 10) < 10) break;

                // 获取帧ID（4字节）
                string frameId = Encoding.ASCII.GetString(frameHeader, 0, 4);

                // 严格验证帧ID
                if (!IsValidFrameId(frameId))
                {
                    Debug.WriteLine($"遇到无效帧ID: '{frameId}' at position {frameStartPosition}，停止解析");
                    break;
                }

                // 获取帧大小（4字节）
                byte[] frameSizeBytes = new byte[4];
                Array.Copy(frameHeader, 4, frameSizeBytes, 0, 4);

                int frameSize;
                int originalFrameSize;

                // 优先使用规范定义的解码方式
                if (majorVersion >= 4)
                {
                    frameSize = originalFrameSize = DecodeSynchsafeInteger(frameSizeBytes);
                }
                else // ID3v2.3 或 ID3v2.2
                {
                    // 默认使用 ID3v2.3 的普通整数解码
                    frameSize = originalFrameSize = DecodeInteger(frameSizeBytes);

                    // 【关键增强：如果帧大小不合理且版本是 v2.3，尝试回退到同步安全解码】
                    long remainingBytes = endPosition - stream.Position;
                    if (majorVersion == 3 && frameSize > remainingBytes)
                    {
                        // 尝试使用 Synchsafe 方式重新解码，以修复错误的编码
                        int synchsafeFrameSize = DecodeSynchsafeInteger(frameSizeBytes);

                        // 如果同步安全解码的结果更合理 (小于剩余字节，且大于 0)，则接受它
                        if (synchsafeFrameSize > 0 && synchsafeFrameSize <= remainingBytes)
                        {
                            Debug.WriteLine($"帧 {frameId} (v2.3) 发现异常大小 ({frameSize})，回退到同步安全大小: {synchsafeFrameSize}");
                            frameSize = synchsafeFrameSize;
                        }
                    }
                }

                // 【新增：智能帧边界检测】
                // 如果是文本帧，尝试通过搜索下一个有效帧ID来确定实际边界
                if (IsTextFrame(frameId) && frameSize > 50) // 只对较大的文本帧进行边界检测
                {
                    int detectedSize = DetectActualFrameSize(stream, frameSize, endPosition);
                    if (detectedSize > 0 && detectedSize != frameSize)
                    {
                        Debug.WriteLine($"帧 {frameId} 检测到实际大小 {detectedSize}，原始大小 {frameSize}");
                        frameSize = detectedSize;
                    }
                }

                // 获取帧标志（2字节）
                byte[] frameFlags = new byte[2];
                Array.Copy(frameHeader, 8, frameFlags, 0, 2);

                Debug.WriteLine($"解析帧: {frameId}, 大小: {frameSize}");

                // 验证帧大小的合理性
                long maxReasonableSize = endPosition - stream.Position;
                if (frameSize <= 0 || frameSize > maxReasonableSize || frameSize > tagDataSize)
                {
                    Debug.WriteLine($"无效的帧大小: {frameSize}，最大合理大小: {maxReasonableSize}，停止解析");
                    break;
                }

                // 确保有足够的数据可读
                if (stream.Position + frameSize > endPosition)
                {
                    Debug.WriteLine($"帧 {frameId} 大小 {frameSize} 超出标签边界，停止解析");
                    break;
                }

                // 读取帧数据
                byte[] frameData = new byte[frameSize];
                int actualRead = stream.Read(frameData, 0, frameSize);
                if (actualRead < frameSize)
                {
                    Debug.WriteLine($"帧 {frameId} 读取不完整: 期望 {frameSize}，实际 {actualRead}");
                    break;
                }

                // 根据帧类型进行解析
                if (frameId == "APIC" || frameId == "PIC") // PIC 是 ID3v2.2 的图片帧
                {
                    var picture = DecodeApicFrame(frameData, majorVersion);
                    if (picture != null)
                    {
                        pictures.Add(picture);
                        Debug.WriteLine($"  {frameId}: {picture.MimeType}, {picture.PictureTypeName}, " +
                                      $"描述: '{picture.Description}', 大小: {picture.ImageData.Length} 字节");
                    }
                }
                else if (IsTextFrame(frameId))
                {
                    string value = DecodeTextFrame(frameData);
                    textTags[frameId] = value;
                    Debug.WriteLine($"  {frameId}: {value}");
                }
                else
                {
                    Debug.WriteLine($"  跳过未知帧类型: {frameId}");
                }

                // 验证当前位置是否正确
                long expectedPosition = frameStartPosition + 10 + frameSize;
                if (stream.Position != expectedPosition)
                {
                    Debug.WriteLine($"警告: 流位置不匹配，期望: {expectedPosition}，实际: {stream.Position}");
                    stream.Seek(expectedPosition, SeekOrigin.Begin);
                }
            }

            return (textTags, pictures);
        }

        // 检查是否为文本帧
        private static bool IsTextFrame(string frameId)
        {
            return frameId.StartsWith("T") && !frameId.Equals("TXXX");
        }

        // --- 主要读取方法：执行双向搜索 ---
        public static Id3v2ParseResult ReadId3v2TagsFromDff(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                long fileSize = stream.Length;
                long headerPosition = -1;

                // 1. 首先尝试在 DFF 结构中查找 ID3 Chunk
                headerPosition = FindDsdiffId3Chunk(stream);

                if (headerPosition == -1)
                {
                    // 2. 如果没找到 ID3 Chunk，在文件头部区域搜索
                    long searchEnd = Math.Min(fileSize, 4 * 1024);
                    headerPosition = FindId3v2Header(stream, 0, searchEnd);
                }

                if (headerPosition == -1)
                {
                    Debug.WriteLine("在文件中未找到 ID3v2 标识符。");
                    return new Id3v2ParseResult();
                }

                // 3. 找到位置后，开始解析
                Debug.WriteLine($"找到 ID3v2 头部于文件位置: {headerPosition} (0x{headerPosition:X})");
                stream.Seek(headerPosition, SeekOrigin.Begin);

                // 读取 10 字节 ID3v2 头部
                byte[] headerBytes = new byte[10];
                if (stream.Read(headerBytes, 0, 10) < 10)
                {
                    Debug.WriteLine("无法读取完整的 ID3v2 头部");
                    return new Id3v2ParseResult();
                }

                // 验证 ID3 标识符
                string identifier = Encoding.ASCII.GetString(headerBytes, 0, 3);
                if (identifier != "ID3")
                {
                    Debug.WriteLine("无效的 ID3v2 标识符");
                    return new Id3v2ParseResult();
                }

                // 获取版本信息
                byte majorVersion = headerBytes[3];
                byte minorVersion = headerBytes[4];
                byte flags = headerBytes[5];

                // 获取标签大小
                byte[] sizeBytes = new byte[4];
                Array.Copy(headerBytes, 6, sizeBytes, 0, 4);
                int tagDataSize = DecodeSynchsafeInteger(sizeBytes);

                Debug.WriteLine($"ID3v2 版本: v2.{majorVersion}.{minorVersion}，数据大小: {tagDataSize} 字节");

                // 检查是否有扩展头部
                bool hasExtendedHeader = (flags & 0x40) != 0;
                if (hasExtendedHeader)
                {
                    // 读取扩展头部大小并跳过
                    byte[] extHeaderSizeBytes = new byte[4];
                    if (stream.Read(extHeaderSizeBytes, 0, 4) == 4)
                    {
                        int extHeaderSize = DecodeSynchsafeInteger(extHeaderSizeBytes);
                        stream.Seek(extHeaderSize - 4, SeekOrigin.Current); // 跳过扩展头部
                        Debug.WriteLine($"跳过扩展头部，大小: {extHeaderSize} 字节");
                    }
                }

                // 解析帧数据
                var (textTags, pictures) = ParseId3v2Frames(stream, tagDataSize, majorVersion);

                return new Id3v2ParseResult
                {
                    TextTags = textTags,
                    Pictures = pictures,
                    MajorVersion = majorVersion,
                    MinorVersion = minorVersion,
                    TagSize = tagDataSize
                };
            }
        }

        private static bool IsValidFrameId(string frameId)
        {
            if (string.IsNullOrEmpty(frameId) || frameId.Length != 4)
                return false;

            // ID3v2 帧ID应该只包含大写字母A-Z和数字0-9
            foreach (char c in frameId)
            {
                if (c == 0 || c == 0xFF) // 填充字节
                    return false;

                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    return false;
            }

            return true;
        }

        private static int DetectActualFrameSize(FileStream stream, int declaredSize, long endPosition)
        {
            long currentPos = stream.Position;
            long maxSearchPos = Math.Min(currentPos + declaredSize, endPosition - 4);

            try
            {
                // 在声明的帧大小范围内搜索下一个有效的帧ID
                for (long searchPos = currentPos + 10; searchPos <= maxSearchPos - 4; searchPos++)
                {
                    stream.Seek(searchPos, SeekOrigin.Begin);

                    byte[] potentialFrameId = new byte[4];
                    if (stream.Read(potentialFrameId, 0, 4) == 4)
                    {
                        string frameId = Encoding.ASCII.GetString(potentialFrameId);

                        // 检查是否是有效的帧ID
                        if (IsValidFrameId(frameId) && IsKnownFrameId(frameId))
                        {
                            // 找到可能的下一个帧，计算实际大小
                            int actualSize = (int)(searchPos - currentPos);
                            stream.Seek(currentPos, SeekOrigin.Begin); // 恢复位置
                            return actualSize;
                        }
                    }
                }
            }
            catch
            {
                // 搜索过程中出错，恢复到原始位置
            }

            stream.Seek(currentPos, SeekOrigin.Begin); // 恢复位置
            return -1; // 未找到边界
        }

        private static bool IsKnownFrameId(string frameId)
        {
            // 常见的ID3v2帧ID
            string[] knownFrameIds = {
                "TALB", "TBPM", "TCOM", "TCON", "TCOP", "TDAT", "TDLY", "TENC", "TEXT", "TFLT",
                "TIME", "TIT1", "TIT2", "TIT3", "TKEY", "TLAN", "TLEN", "TMED", "TOAL", "TOFN",
                "TOLY", "TOPE", "TORY", "TOWN", "TPE1", "TPE2", "TPE3", "TPE4", "TPOS", "TPUB",
                "TRCK", "TRDA", "TRSN", "TRSO", "TSIZ", "TSRC", "TSSE", "TYER", "TXXX",
                "APIC", "COMM", "GEOB", "PCNT", "POPM", "PRIV", "SYLT", "USLT", "WCOM", "WCOP",
                "WOAF", "WOAR", "WOAS", "WORS", "WPAY", "WPUB", "WXXX"
            };

            return knownFrameIds.Contains(frameId);
        }

        // 兼容性方法：保持原有的方法签名
        public static Dictionary<string, string> ReadId3v2TagsFromDff_TextOnly(string filePath)
        {
            var result = ReadId3v2TagsFromDff(filePath);
            return result.TextTags;
        }

        // 便捷方法：获取常见标签的友好名称
        public static Dictionary<string, string> GetFriendlyTags(Dictionary<string, string> rawTags)
        {
            var friendlyTags = new Dictionary<string, string>();
            var tagMapping = new Dictionary<string, string>
        {
            {"TIT2", "标题"},
            {"TPE1", "艺术家"},
            {"TALB", "专辑"},
            {"TCON", "流派"},
            {"TYER", "年份"},
            {"TDAT", "日期"},
            {"TRCK", "音轨"},
            {"TPE2", "专辑艺术家"},
            {"TPOS", "唱片集"},
            {"TSSE", "编码软件"},
            {"COMM", "评论"},
            {"TIT1", "内容组描述"},
            {"TIT3", "副标题"},
            {"TPE3", "指挥"},
            {"TPE4", "翻译/修改"},
            {"TPUB", "发行商"},
            {"TCOP", "版权"},
            {"TENC", "编码者"}
        };

            foreach (var tag in rawTags)
            {
                string friendlyName = tagMapping.ContainsKey(tag.Key) ? tagMapping[tag.Key] : tag.Key;
                friendlyTags[friendlyName] = tag.Value;
            }

            return friendlyTags;
        }
    }    
}
