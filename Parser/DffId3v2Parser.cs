using ManagedBass;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ZLinq;

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

        /// <summary>
        /// 从文件尾部向文件开头倒序搜索 ID3v2 头部（"ID3" 标识符）。
        /// </summary>
        private static long FindId3v2HeaderReverse(FileStream stream, long fileEnd, long maxSearchOffset)
        {
            // 确保 startOffset >= endOffset，因为我们是倒序搜索
            if (fileEnd < maxSearchOffset)
            {
                // 交换以确保逻辑正确，或者直接返回 -1
                // 这里假设调用者可能错误地传递了参数，进行修正
                long temp = fileEnd;
                fileEnd = maxSearchOffset;
                maxSearchOffset = temp;
            }

            long searchLength = fileEnd - maxSearchOffset;
            // 至少需要 3 个字节来容纳 "ID3"
            if (searchLength < 3) return -1;

            // 设置搜索块大小（例如 4MB）
            // 为了倒序搜索，我们一次性读取一个搜索块，然后从后往前搜索
            int bufferSize = (int)Math.Min(searchLength, 4 * 1024 * 1024);
            byte[] buffer = new byte[bufferSize];

            // 确定读取的起始位置。我们从 (startOffset - bufferSize) 处开始读取
            long readStartOffset = fileEnd - bufferSize;

            // 确保读取不会超过文件开头，即不小于 endOffset
            if (readStartOffset < maxSearchOffset)
            {
                readStartOffset = maxSearchOffset;
                bufferSize = (int)(fileEnd - maxSearchOffset); // 实际读取长度
                if (bufferSize < 3) return -1; // 再次检查最小长度
            }

            // 设置流位置到实际开始读取的位置
            stream.Seek(readStartOffset, SeekOrigin.Begin);
            int bytesRead = stream.Read(buffer, 0, bufferSize);

            // 确保至少读到了 3 个字节
            if (bytesRead < 3) return -1;

            // 倒序遍历缓冲区，查找 "ID3" 标识符
            // 从 bytesRead - 3 开始，因为我们需要检查 buffer[i], buffer[i+1], buffer[i+2]
            for (int i = bytesRead - 3; i >= 0; i--)
            {
                if (buffer[i] == 'I' && buffer[i + 1] == 'D' && buffer[i + 2] == '3')
                {
                    // 找到了 ID3 头部
                    // 返回相对于文件开头的绝对偏移量
                    return readStartOffset + i;
                }
            }

            // 未在当前搜索块中找到
            return -1;
        }

        // 检查是否为文本帧
        private static bool IsTextFrame(string frameId)
        {
            return frameId.StartsWith("T") && !frameId.Equals("TXXX");
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

            return knownFrameIds.AsValueEnumerable().Contains(frameId);
        }

        /// <summary>
        /// 专门用于解析文件末尾 4MB 区域内的 ID3v2 标签，并仅提取图片数据。
        /// </summary>
        /// <param name="filePath">DFF 文件路径。</param>
        /// <returns>找到的所有图片数据的字节数组列表。</returns>
        public static Id3v2ParseResult ReadId3v2TagsFromDff(string filePath,bool FromFront = false)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                long fileSize = stream.Length;
                long searchStart = Math.Max(0, fileSize - 1024 * 1024);
                long searchEnd = fileSize;
                long currentSearchPosition = searchEnd;
                long headerPosition = -1;
                if (FromFront)
                {
                    searchStart = 0;
                    searchEnd = Math.Min(fileSize, 4 * 1024);
                    currentSearchPosition = searchStart;                    
                }
                while (FromFront ? currentSearchPosition < searchEnd - 10 : currentSearchPosition> searchStart+10)
                {
                    // 1. 查找 ID3 标识符
                    if (headerPosition == -1)
                    {
                        headerPosition = FromFront ? FindId3v2Header(stream, currentSearchPosition, searchEnd) : FindId3v2HeaderReverse(stream, currentSearchPosition, searchStart);
                    }


                    if (headerPosition == -1)
                    {
                        Debug.WriteLine("未找到更多 ID3v2 标识符。");
                        return new Id3v2ParseResult();
                    }

                    stream.Seek(headerPosition, SeekOrigin.Begin);

                    byte[] headerBytes = new byte[10];
                    if (stream.Read(headerBytes, 0, 10) < 10)
                    {
                        currentSearchPosition = FromFront ? headerPosition + 3:headerPosition - 3; // 移动到标识符之后继续搜
                        continue;
                    }

                    byte majorVersion = headerBytes[3];
                    byte flags = headerBytes[5];

                    // --- 2. 关键验证 ---
                    // ID3v2 版本号通常是 2, 3, 或 4。我们排除 32 (0x20)
                    if (majorVersion < 2 || majorVersion > 4)
                    {
                        Debug.WriteLine($"找到无效版本号 v2.{majorVersion} (0x{majorVersion:X2})，跳过并继续搜索。");
                        currentSearchPosition = FromFront ? headerPosition + 3 : headerPosition - 3;  // 移动到 ID3 标识符之后继续搜
                        continue;
                    }

                    // --- 3. 头部有效，开始正常解析 ---
                    Debug.WriteLine($"找到有效的 ID3v2 头部于位置: {headerPosition} (v2.{majorVersion})");
                    // 获取标签大小
                    byte[] sizeBytes = new byte[4];
                    Array.Copy(headerBytes, 6, sizeBytes, 0, 4);

                    int tagDataSize = (majorVersion >= 4) ? DecodeSynchsafeInteger(sizeBytes) : DecodeInteger(sizeBytes);

                    Debug.WriteLine($"数据大小: {tagDataSize} 字节");

                    // 我们传入标签大小，但 ParseId3v2FramesForPictures 内部会用文件长度来限制它。
                    var (textTags, pictures) = ParseId3v2Frames(stream, tagDataSize, majorVersion);

                    return new Id3v2ParseResult
                    {
                        TextTags = textTags,
                        Pictures = pictures
                    };
                }

                return new Id3v2ParseResult(); // 如果循环结束都没有找到
            }
        }

        /// <summary>
        /// 专门用于解析 ID3v2 帧数据，但只处理图片帧 (APIC/PIC)。
        /// 忽略所有文本帧和其它帧，避免因非标准 T-帧导致解析失败。
        /// </summary>
        private static (Dictionary<string, string> textTags, List<Id3v2Picture> pictures) ParseId3v2Frames(FileStream stream, int tagDataSize, byte majorVersion)
        {
            var textTags = new Dictionary<string, string>();
            var pictures = new List<Id3v2Picture>();
            long startPosition = stream.Position;
            long endPosition = startPosition + tagDataSize;
            long maxLimit = Math.Min(endPosition, stream.Length);

            while (stream.Position < maxLimit - 10) // 至少需要 10 字节的帧头
            {
                long frameStartPosition = stream.Position;

                // 读取帧头
                byte[] frameHeader = new byte[10];
                if (stream.Read(frameHeader, 0, 10) < 10) break;

                string frameId = Encoding.ASCII.GetString(frameHeader, 0, 4);

                // 遇到填充字节 (0x00) 立即停止，这是 ID3v2 标签的结束标志
                if (frameHeader[0] == 0)
                {
                    Debug.WriteLine("遇到填充字节 (0x00)，停止解析。");
                    break;
                }

                // 遇到无效帧ID也停止
                if (!IsValidFrameId(frameId))
                {
                    Debug.WriteLine($"遇到无效帧ID ('{frameId}')，停止解析。");
                    break;
                }

                // 获取帧大小
                byte[] frameSizeBytes = new byte[4];
                Array.Copy(frameHeader, 4, frameSizeBytes, 0, 4);

                int frameSize;
                // 帧大小解码：根据版本选择
                if (majorVersion >= 4)
                {
                    frameSize = DecodeSynchsafeInteger(frameSizeBytes);
                }
                else
                {
                    frameSize = DecodeInteger(frameSizeBytes);
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
                
                if (frameId == "APIC" || frameId == "PIC")
                {
                    if (actualRead == frameSize)
                    {
                        var picture = DecodeApicFrame(frameData, majorVersion);
                        if (picture != null)
                        {
                            pictures.Add(picture);
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"图片帧 {frameId} 读取不完整，跳过。");
                        break;
                    }
                }
                else if(IsTextFrame(frameId))
                {
                    string value = DecodeTextFrame(frameData);
                    Debug.WriteLine($"  {frameId}: {value}");
                    textTags[frameId] = value;
                    //stream.Seek(frameSize, SeekOrigin.Current);
                }

                // 检查跳过操作是否超出边界
                if (stream.Position > maxLimit)
                {
                    Debug.WriteLine($"警告：跳过操作使流位置超出标签/文件边界，停止。");
                    break;
                }
            }

            return (textTags,pictures);
        }
    }    
}
