using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AudioDecoding.Shared;
using FFmpeg.AutoGen;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

internal readonly record struct CueFileRevision(long Length, long LastWriteUtcTicks)
{
    public static CueFileRevision Read(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    public bool Matches(string path)
    {
        try { return Read(path) == this; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

internal sealed record CueInput(string Path, CueFileRevision Revision, int SampleRate, long TotalSamples, int BitDepth,
    string? CodecId, Music Metadata);
internal sealed record CueText(string Path, CueFileRevision Revision, string Fingerprint, byte[] Bytes);

internal sealed record CueSheetReader(string Path, CueFileRevision Revision, string Fingerprint, string EncodingName,
    List<CueInput> Sources, List<Music> Tracks)
{
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumLines = 16_384;
    private const int LosslessEncodingKind = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly unsafe AVIOInterruptCB_callback StopSourceProbe = static opaque =>
        Stopwatch.GetTimestamp() >= *(long*)opaque ? 1 : 0;

    public bool IsCurrent()
    {
        if (!Revision.Matches(Path)) return false;
        foreach (var source in Sources)
            if (!source.Revision.Matches(source.Path) || AudioFileWriteGate.IsBeingWritten(source.Path)) return false;
        return true;
    }

    public static async Task<CueText> ReadTextAsync(string path, CancellationToken token)
    {
        path = System.IO.Path.GetFullPath(path);
        var revision = CueFileRevision.Read(path);
        if (revision.Length is <= 0 or > MaximumBytes) throw Invalid("Unsupported CUE size");
        byte[] bytes = new byte[(int)revision.Length];
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (file.Length != bytes.Length) throw Invalid("CUE changed during read");
        }
        return new CueText(path, revision, Convert.ToHexString(SHA256.HashData(bytes)), bytes);
    }

    public static async Task<CueSheetReader> ReadAsync(CueText input, Dictionary<string, CueInput> sourceCache,
        CancellationToken token)
    {
        string path = input.Path;
        (string text, string encoding) = Decode(input.Bytes);
        if (text.Contains('\0')) throw Invalid("CUE contains a NUL character");

        string directory = System.IO.Path.GetDirectoryName(path)!;
        var tracks = Parse(text, directory, token, out var album);
        var sources = new List<CueInput>();
        var music = new List<Music>(tracks.Count);
        for (int first = 0; first < tracks.Count;)
        {
            token.ThrowIfCancellationRequested();
            var track = tracks[first];
            if (!sourceCache.TryGetValue(track.Path, out var source))
            {
                if (AudioFileWriteGate.IsBeingWritten(track.Path)) throw Invalid("CUE source is being written");
                if ((File.GetAttributes(track.Path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw Invalid("CUE source must be a regular file in the CUE directory");
                var sourceRevision = CueFileRevision.Read(track.Path);
                var (rate, samples, bitDepth, codecId) = ReadPcmLength(track.Path, token);
                var (metadata, _) = await AddFolderService.ReadMusicAsync(track.Path, token).ConfigureAwait(false);
                if (metadata is null || metadata.SampleRate > 0 && metadata.SampleRate != rate)
                    throw Invalid("CUE source metadata is unavailable or inconsistent");
                sourceCache.Add(track.Path, source = new(track.Path, sourceRevision, rate, samples, bitDepth, codecId, metadata));
            }
            sources.Add(source);
            int last = first + 1;
            while (last < tracks.Count && string.Equals(tracks[last].Path, track.Path, StringComparison.OrdinalIgnoreCase)) last++;
            for (int i = first; i < last; i++)
            {
                var entry = tracks[i];
                long start = ToSample(entry.Index01, source.SampleRate);
                long end = i + 1 == last ? source.TotalSamples : ToSample(tracks[i + 1].Index01, source.SampleRate);
                if (start >= end || end > source.TotalSamples) throw Invalid($"TRACK {entry.Number:D2} exceeds the source or has no samples");
                music.Add(CreateTrack(path, album, entry, source, start, end));
            }
            first = last;
        }
        var sheet = new CueSheetReader(path, input.Revision, input.Fingerprint, encoding, sources, music);
        if (!sheet.IsCurrent()) throw Invalid("CUE or audio changed during read");
        return sheet;
    }

    private static long ToSample(long frames, int rate) => checked(frames / 75 * rate + frames % 75 * rate / 75);

    private static Music CreateTrack(string sheet, Tags album, CueTrack track, CueInput source, long start, long end)
    {
        Music metadata = source.Metadata;
        long durationTicks = checked((long)((Int128)(end - start) * TimeSpan.TicksPerSecond / source.SampleRate));
        return new Music
        {
            CuePath = sheet,
            CueTrackNo = track.Number,
            CueSourceTitle = track.Tags.Title,
            StartSample = start,
            EndSample = end,
            Path = source.Path,
            Title = First(track.Tags.Title, $"Track {track.Number:D2}"),
            TitleFromFileName = string.IsNullOrWhiteSpace(track.Tags.Title),
            Author = First(track.Tags.Performer, album.Performer, metadata.Author),
            Album = First(album.Title, metadata.Album),
            AlbumArtist = First(album.Performer, metadata.AlbumArtist),
            Genre = First(track.Tags.Genre, album.Genre, metadata.Genre),
            Composer = metadata.Composer,
            Lyricist = metadata.Lyricist,
            Conductor = metadata.Conductor,
            CatalogNumber = metadata.CatalogNumber,
            AdditionalMetadataRead = true,
            Year = Positive(track.Tags.Year, album.Year, metadata.Year),
            TrackNumber = track.Number,
            DiskNumber = Positive(track.Tags.Disc, album.Disc, metadata.DiskNumber),
            SampleRate = source.SampleRate,
            Duration = TimeSpan.FromTicks(durationTicks),
            FolderPath = metadata.FolderPath,
            LastLevelFolderPath = metadata.LastLevelFolderPath,
            Extension = metadata.Extension,
            CodecId = source.CodecId ?? metadata.CodecId,
            EncodingKind = LosslessEncodingKind,
            BitRate = metadata.BitRate,
            BitDepth = source.BitDepth > 0 ? source.BitDepth : metadata.BitDepth,
            Channel = metadata.Channel,
            CreateTime = metadata.CreateTime,
            UpdateTime = metadata.UpdateTime
        };
    }

    private static string First(string first, string second, string third = "") =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : third;

    private static int Positive(int first, int second, int third) => first > 0 ? first : second > 0 ? second : third;

    private static List<CueTrack> Parse(string text, string directory, CancellationToken token, out Tags album)
    {
        using var lines = new StringReader(text);
        var tracks = new List<CueTrack>();
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTracks = new HashSet<int>();
        string? source = null;
        CueTrack? track = null;
        bool hasTrackForSource = false;
        album = new();
        int lineNumber = 0;
        while (lines.ReadLine() is { } raw)
        {
            token.ThrowIfCancellationRequested();
            if (++lineNumber > MaximumLines) throw Invalid("CUE line limit exceeded");
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            ReadOnlySpan<char> directive = NextToken(ref line);
            if (directive.Equals("FILE", StringComparison.OrdinalIgnoreCase))
            {
                if (source is not null && !hasTrackForSource) throw Invalid("FILE has no tracks");
                string file = NextFileName(line);
                if (file.Length == 0 || file.IndexOfAny(['/', '\\', ':']) >= 0 || file is "." or "..")
                    throw Invalid("FILE must reference an audio file in the CUE directory");
                source = System.IO.Path.Combine(directory, file);
                if (!usedSources.Add(source)) throw Invalid("CUE repeats a FILE declaration");
                track = null;
                hasTrackForSource = false;
            }
            else if (directive.Equals("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                if (source is null || !ParseInt(NextToken(ref line), out int number) || number is < 1 or > 99 ||
                    !line.Trim().Equals("AUDIO", StringComparison.OrdinalIgnoreCase) || !usedTracks.Add(number))
                    throw Invalid($"Invalid or non-audio TRACK at line {lineNumber}");
                track = new(source, number, tracks.Count > 0 && hasTrackForSource ? tracks[^1] : null);
                tracks.Add(track);
                hasTrackForSource = true;
            }
            else if (directive.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            {
                if (track is null || !ParseInt(NextToken(ref line), out int index) || index is < 0 or > 99)
                    throw Invalid($"Invalid INDEX at line {lineNumber}");
                long frames = ParseFrames(line.Trim());
                if (index == 0 && track.Index00 < 0 && track.Index01 < 0) track.Index00 = frames;
                else if (index == 1 && track.Index01 < 0) track.Index01 = frames;
                else if (index <= 1) throw Invalid($"Duplicate or unordered INDEX at line {lineNumber}");
            }
            else
            {
                var tags = track?.Tags ?? album;
                if (directive.Equals("TITLE", StringComparison.OrdinalIgnoreCase)) tags.Title = TextValue(line);
                else if (directive.Equals("PERFORMER", StringComparison.OrdinalIgnoreCase)) tags.Performer = TextValue(line);
                else if (directive.Equals("REM", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> key = NextToken(ref line);
                    if (key.Equals("GENRE", StringComparison.OrdinalIgnoreCase)) tags.Genre = TextValue(line);
                    else if (key.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadOnlySpan<char> year = TextValue(line);
                        if (year.Length >= 4 && ParseInt(year[..4], out int value)) tags.Year = value;
                    }
                    else if (key.Equals("DISCNUMBER", StringComparison.OrdinalIgnoreCase))
                    {
                        ReadOnlySpan<char> disc = TextValue(line);
                        int slash = disc.IndexOf('/');
                        if (slash >= 0) disc = disc[..slash];
                        if (ParseInt(disc, out int value)) tags.Disc = value;
                    }
                }
            }
        }
        if (tracks.Count == 0 || !hasTrackForSource) throw Invalid("CUE contains no complete audio FILE");
        foreach (var entry in tracks)
        {
            if (entry.Index01 < 0 || entry.Index00 > entry.Index01 ||
                entry.Previous is { } prior && (prior.Index01 >= entry.Index01 || entry.Index00 >= 0 && entry.Index00 < prior.Index01))
                throw Invalid($"Missing or unordered INDEX 01 for TRACK {entry.Number:D2}");
        }
        return tracks;
    }

    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> line)
    {
        line = line.TrimStart();
        int length = 0;
        while (length < line.Length && !char.IsWhiteSpace(line[length])) length++;
        var token = line[..length];
        line = line[length..].TrimStart();
        return token;
    }

    private static string NextFileName(ReadOnlySpan<char> line)
    {
        // The FILE type describes the catalog entry and may say WAVE for a FLAC stream.
        int split = line.LastIndexOfAny(' ', '\t');
        if (split < 0) throw Invalid("Missing FILE type");
        var name = line[..split].Trim();
        if (name.Length > 0 && (name[0] == '"' || name[^1] == '"') && (name.Length < 2 || name[0] != '"' || name[^1] != '"'))
            throw Invalid("Invalid quoted FILE name");
        return TextValue(name);
    }

    private static string TextValue(ReadOnlySpan<char> value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? new(value[1..^1]) : new(value.Trim());

    private static long ParseFrames(ReadOnlySpan<char> time)
    {
        int first = time.IndexOf(':');
        int last = time.LastIndexOf(':');
        if (first < 1 || last <= first || !ParseInt(time[..first], out int minute) ||
            !ParseInt(time[(first + 1)..last], out int second) || second >= 60 ||
            !ParseInt(time[(last + 1)..], out int frame) || frame >= 75)
            throw Invalid("Invalid INDEX time code");
        return ((long)minute * 60 + second) * 75 + frame;
    }

    private static bool ParseInt(ReadOnlySpan<char> value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static (string, string) Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (StrictUtf8.GetString(span[3..]), "UTF-8 BOM");
        if (span.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return (new UTF32Encoding(false, false, true).GetString(span[4..]), "UTF-32 LE");
        if (span.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return (new UTF32Encoding(true, false, true).GetString(span[4..]), "UTF-32 BE");
        if (span.StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true).GetString(span[2..]), "UTF-16 LE");
        if (span.StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true).GetString(span[2..]), "UTF-16 BE");
        try { return (StrictUtf8.GetString(span), "UTF-8"); }
        catch (DecoderFallbackException) { }

        string? japanese = TryDecode(932);
        string? simplified = TryDecode(936);
        string? traditional = TryDecode(950);
        if (japanese is not null && (simplified is null || japanese.Any(static c => c is >= '\u3041' and <= '\u30FF')))
            return (japanese, "Windows-932");
        if (simplified is not null) return (simplified, "Windows-936");
        if (traditional is not null) return (traditional, "Windows-950");
        throw Invalid("Unsupported CUE text encoding");

        string? TryDecode(int codePage)
        {
            try
            {
                var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return encoding?.GetString(bytes);
            }
            catch (DecoderFallbackException) { return null; }
        }
    }

    private static (int Rate, long Samples, int BitDepth, string? CodecId) ReadPcmLength(string path, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        if (header[..4].SequenceEqual("fLaC"u8))
        {
            if ((header[4] & 0x7F) != 0 || header[5] != 0 || header[6] != 0 || header[7] != 34)
                throw Invalid("Invalid FLAC STREAMINFO");
            file.Position = 18;
            file.ReadExactly(header[..8]);
            ulong format = BinaryPrimitives.ReadUInt64BigEndian(header);
            int rate = (int)(format >> 44);
            int bits = (int)((format >> 36) & 0x1F) + 1;
            long samples = (long)(format & 0xFFFFFFFFF);
            if (rate <= 0 || samples <= 0 || file.Length <= 42) throw Invalid("FLAC stream has no exact sample count");
            return (rate, samples, bits, null);
        }
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            return ReadAdditionalPcmLength(path, token);

        long limit = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (limit > file.Length) throw Invalid("Incomplete WAV source");
        int rateWav = 0, blockAlign = 0, bitDepth = 0;
        long dataLength = 0;
        Span<byte> fmt = stackalloc byte[40];
        while (file.Position + 8 <= limit)
        {
            file.ReadExactly(header[..8]);
            long length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long next = file.Position + length + (length & 1);
            if (next > limit) throw Invalid("Incomplete WAV chunk");
            if (header[..4].SequenceEqual("fmt "u8))
            {
                if (length < 16) throw Invalid("Incomplete WAV format");
                file.ReadExactly(fmt[..(int)Math.Min(length, fmt.Length)]);
                int format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == 0xFFFE && length >= 40 && fmt[26..40].SequenceEqual(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 }))
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                    int validBits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]);
                    if (validBits > 0 && validBits <= bitDepth) bitDepth = validBits;
                }
                if (format is not (1 or 3)) throw Invalid("WAV source is not PCM");
                rateWav = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
            }
            else if (header[..4].SequenceEqual("data"u8)) dataLength = length;
            if (rateWav > 0 && blockAlign > 0 && dataLength > 0) break;
            file.Position = next;
        }
        if (rateWav <= 0 || blockAlign <= 0 || dataLength < blockAlign || dataLength % blockAlign != 0)
            throw Invalid("WAV stream has no exact sample count");
        return (rateWav, dataLength / blockAlign, bitDepth, null);
    }

    private static unsafe (int Rate, long Samples, int BitDepth, string? CodecId) ReadAdditionalPcmLength(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        AVFormatContext* format = null;
        AVDictionary* options = null;
        try
        {
            format = ffmpeg.avformat_alloc_context();
            if (format == null) throw Invalid("Could not create the CUE source probe");
            format->interrupt_callback = new AVIOInterruptCB { callback = StopSourceProbe, opaque = &deadline };
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            ffmpeg.av_dict_set(&options, "format_whitelist", "aiff,ape,wv,mov", 0);
            CuePcmSource.SetInputOptions(&options);
            if (ffmpeg.avformat_open_input(&format, path, null, &options) < 0 || format == null)
                throw Invalid("Unsupported or incomplete CUE audio source");

            // The demuxer's header provides the source frame count. Stream-info probing
            // can substitute a duration estimate and read packets, neither is needed here.
            int audio = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audio < 0 || !CuePcmSource.TryGetSampleCount(format, format->streams[audio], path, out long samples, out int headerBitDepth))
                throw Invalid("CUE audio source has no supported, exact sample count");
            var parameters = format->streams[audio]->codecpar;
            if (!TryReadDecoderBitDepth(parameters, out int decoderBitDepth))
                throw Invalid("The CUE audio source codec configuration is unsupported");
            token.ThrowIfCancellationRequested();
            int bitDepth = headerBitDepth > 0 ? headerBitDepth : decoderBitDepth;
            string? codecId = parameters->codec_id == AVCodecID.AV_CODEC_ID_ALAC ? "ALAC" : null;
            return (parameters->sample_rate, samples, bitDepth, codecId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or TypeInitializationException or NotSupportedException)
        {
            throw new InvalidDataException("The CUE audio format probe is unavailable", ex);
        }
        finally
        {
            if (options != null) ffmpeg.av_dict_free(&options);
            if (format != null) ffmpeg.avformat_close_input(&format);
        }
    }

    private static unsafe bool TryReadDecoderBitDepth(AVCodecParameters* parameters, out int bitDepth)
    {
        // The decoder can reject a profile, bit depth or channel layout after its
        // codec ID has matched. Initialize it from headers without decoding packets.
        bitDepth = 0;
        var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder == null) return false;
        var context = ffmpeg.avcodec_alloc_context3(decoder);
        if (context == null) return false;
        try
        {
            context->thread_count = 1;
            if (ffmpeg.avcodec_parameters_to_context(context, parameters) < 0
                || ffmpeg.avcodec_open2(context, decoder, null) < 0
                || context->sample_rate != parameters->sample_rate)
                return false;
            bitDepth = context->bits_per_raw_sample > 0 ? context->bits_per_raw_sample : context->bits_per_coded_sample;
            if (bitDepth <= 0) bitDepth = ffmpeg.av_get_bits_per_sample(parameters->codec_id);
            return true;
        }
        finally { ffmpeg.avcodec_free_context(&context); }
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Tags
    {
        public string Title = "";
        public string Performer = "";
        public string Genre = "";
        public int Year;
        public int Disc;
    }

    private sealed record CueTrack(string Path, int Number, CueTrack? Previous)
    {
        public Tags Tags { get; } = new();
        public long Index00 = -1;
        public long Index01 = -1;
    }
}
