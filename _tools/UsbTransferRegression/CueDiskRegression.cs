using System.Buffers.Binary;
using System.Security.Cryptography;
using FFmpeg.AutoGen;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

internal static class CueDiskRegression
{
    public static async Task RunAsync()
    {
        ffmpeg.RootPath = AppContext.BaseDirectory;
        string scratch = Path.Combine(AppContext.BaseDirectory, "test-data", $"CueRegression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var database = new MusicDatabaseService();
        try
        {
            string sourceWav = Path.Combine(scratch, "Album.wav");
            string source = Path.Combine(scratch, "Album.flac");
            const int rate = 44100, start = 44137, end = 88200 - 19;
            byte[] waveform = new byte[rate * 2 * 4];
            for (int frame = 0; frame < rate * 2; frame++)
            {
                short left = (short)(12000 * Math.Sin(2 * Math.PI * frame * (frame < start ? 344 : 912) / rate));
                BinaryPrimitives.WriteInt16LittleEndian(waveform.AsSpan(frame * 4), left);
                BinaryPrimitives.WriteInt16LittleEndian(waveform.AsSpan(frame * 4 + 2), (short)-left);
            }
            WriteWave(sourceWav, rate, waveform);
            new FFmpegAudioConverter().Convert(sourceWav, source, "flac");
            byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            byte[] cover = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ6sAAAAASUVORK5CYII=");
            ToolUtils.CoverBytes = cover;
            await database.InitializeAsync(Path.Combine(scratch, "usb.db"));
            var converter = new AudioConverterService(new AppViewModel(), WinUIMusicPlayer.App.GetLogger<AudioConverterService>());
            var writer = new UsbWriterHelper(converter, database);
            var target = new UsbStorageDevice { Name = "fixture-disk", UniqueId = "fixture-disk", Path = Path.Combine(scratch, "device") };
            string cuePath = Path.Combine(scratch, "Album.cue");
            var first = new Music(source, "Kano", "Demo") {
                Id = 501, CueTrackNo = 1, CuePath = cuePath, Title = "同じ曲名", SampleRate = rate, StartSample = 0, EndSample = start
            };
            var second = new Music(source, "Kano", "Demo") {
                Id = 502, CueTrackNo = 2, CuePath = cuePath, Title = first.Title, SampleRate = rate, StartSample = start, EndSample = end
            };

            var sent = await writer.WriteToUsb([first, second], target);
            Check(sent.Succeeded.Count == 2 && sent.Failures.Count == 0, "default export re-encodes both CUE tracks in the source format");
            await database.SaveUsbExportRecordsAsync(sent.Succeeded, target);
            var ledger = await database.GetUsbDeviceMusics(target.UniqueId);
            Check(ledger.Count == 2 && ledger.Select(row => row.Path).Distinct().Count() == 2, "shared source and title produce distinct device paths");
            var firstOnly = ledger.Single(record => record.ExportSourceIdentity == UsbExportPaths.GetSourceIdentity(first));
            // For a normal device the MUSIC paths are rooted at a drive letter.
            firstOnly.Path = UsbExportPaths.GetRelativeFilePath(first, "flac");
            UsbDeviceMusicPresence.Update([first, second], [firstOnly], [first.Title], [Path.GetFileNameWithoutExtension(firstOnly.Path)]);
            Check(first.IsExistOnDevice == 2 && second.IsExistOnDevice == 0, "a device export lights up its CUE track only, even when titles match");

            string secondPath = Path.Combine(target.Path, UsbExportPaths.GetRelativeFilePath(second, "flac"));
            var flacTags = new ATL.Track(secondPath);
            Check(flacTags.Title == second.Title && flacTags.TrackNumber == 2
                && flacTags.EmbeddedPictures.Any(p => p.PictureData.SequenceEqual(cover)), "FLAC export carries track title, number and cover");
            string decodedPath = Path.Combine(scratch, "decoded.wav");
            new FFmpegAudioConverter().Convert(secondPath, decodedPath, "wav");
            byte[] expected = waveform.AsSpan(start * 4, (end - start) * 4).ToArray();
            Check(ReadPcm(decodedPath).SequenceEqual(expected), "FLAC export matches the exact PCM samples at both boundaries");

            var wav = await writer.WriteToUsb([second], target, "wav");
            Check(wav.Succeeded.Count == 1, "WAV export succeeds for a FLAC CUE track");
            string wavPath = Path.Combine(target.Path, UsbExportPaths.GetRelativeFilePath(second, "wav"));
            var wavTags = new ATL.Track(wavPath);
            Check(wavTags.Title == second.Title && wavTags.TrackNumber == 2
                && wavTags.EmbeddedPictures.Any(p => p.PictureData.SequenceEqual(cover)), "WAV export keeps Unicode ID3 tags and cover");
            Check(ReadPcm(wavPath).SequenceEqual(expected), "WAV export and ID3 write preserve the exact selected samples");

            string alac = Path.Combine(scratch, "source.m4a");
            new FFmpegAudioConverter().Convert(sourceWav, alac, "alac");
            var appleTrack = new Music(alac, "Kano", "Apple") {
                Id = 503, CueTrackNo = 2, CuePath = Path.ChangeExtension(alac, ".cue"), Title = second.Title,
                BitDepth = 16, SampleRate = rate, StartSample = start, EndSample = end
            };
            var appleExport = await writer.WriteToUsb([appleTrack], target);
            Check(appleExport.Succeeded.Count == 1 && appleExport.Succeeded[0].Extension == "m4a",
                "USB chooses the ALAC encoder for an M4A CUE entry");
            string applePath = Path.Combine(target.Path, UsbExportPaths.GetRelativeFilePath(appleTrack, "m4a"));
            new FFmpegAudioConverter().Convert(applePath, decodedPath, "wav");
            Check(ReadPcm(decodedPath).SequenceEqual(expected), "USB ALAC contains only the requested samples");

            var wholeApple = new Music(alac, "Kano", "Whole") { Id = 504 };
            var copied = await writer.WriteToUsb([wholeApple], target);
            string copiedPath = Path.Combine(target.Path, UsbExportPaths.GetRelativeFilePath(wholeApple, "m4a"));
            Check(copied.Succeeded.Count == 1 && File.ReadAllBytes(copiedPath).SequenceEqual(File.ReadAllBytes(alac)),
                "sending an ordinary M4A still copies the original bytes");

            foreach (string extension in new[] { "ape", "aiff", "aif", "aifc", "wv" })
            {
                var entry = new Music(Path.Combine(scratch, "source." + extension), "Kano", "Collision") {
                    Id = 505, BitDepth = 16, CueTrackNo = 2, CuePath = Path.Combine(scratch, "toc.cue"), Title = second.Title
                };
                var other = new Music(Path.Combine(scratch, UsbExportPaths.GetCueTrackFileStem(entry) + ".flac"), entry.Author, entry.Album);
                string?[] planned = UsbExportConflicts.Find([entry, other], null, null, [true, true], default);
                Check(planned.All(path => Path.GetExtension(path) == ".flac"), $"CUE {extension} conflict check protects the actual FLAC output for both songs");
            }

            appleTrack.BitDepth = 0;
            Check(UsbExportPaths.GetExportFormat(appleTrack, null) == "wav", "unknown precision uses PCM to preserve the source samples");
            appleTrack.BitDepth = 32;
            Check(UsbExportPaths.GetExportFormat(appleTrack, null) == "wav" && UsbExportPaths.GetExportFormat(appleTrack, "mp3") == "mp3",
                "high-precision CUE defaults to PCM while explicit output format wins");
            string doublePath = Path.Combine(scratch, "Double.wav");
            byte[] doubles = new byte[rate * 2 * sizeof(double)];
            for (int sample = 0; sample < doubles.Length / sizeof(double); sample++)
                BinaryPrimitives.WriteDoubleLittleEndian(doubles.AsSpan(sample * sizeof(double)), Math.Sin(sample * .371) * 1.05);
            WriteWave(doublePath, rate, doubles, 3, 1, 64);
            var doubleTrack = new Music(doublePath, "PCM", "Double") {
                Id = 506, BitDepth = 64, CueTrackNo = 2, CuePath = Path.ChangeExtension(doublePath, ".cue"),
                Title = "Float64", SampleRate = rate, StartSample = start, EndSample = end
            };
            var doubleExport = await writer.WriteToUsb([doubleTrack], target);
            string doubleOutput = Path.Combine(target.Path, UsbExportPaths.GetRelativeFilePath(doubleTrack, "wav"));
            Check(doubleExport.Succeeded.Count == 1 && ReadPcm(doubleOutput).AsSpan().SequenceEqual(
                doubles.AsSpan(start * sizeof(double), (end - start) * sizeof(double))),
                "default USB export and metadata write preserve the exact selected float64 samples");

            var bad = await writer.WriteToUsb([second], target, "unsupported-format");
            Check(bad.Succeeded.Count == 0 && bad.Failures.Count == 1
                && !File.Exists(Path.Combine(Path.GetDirectoryName(secondPath)!, Path.GetFileName(source))),
                "CUE encoder failure reports an audio failure without copying the whole file");
            var conflict = await writer.WriteToUsb([second], target);
            Check(conflict.Succeeded.Count == 0 && conflict.Failures.Count == 1, "CUE export cannot replace an existing destination without a matching ledger entry");
            var record = ledger.Single(item => item.ExportSourceIdentity == UsbExportPaths.GetSourceIdentity(second));
            record.Path = UsbExportPaths.GetRelativeFilePath(second, "flac");
            var repeat = await writer.WriteToUsb([second], target, existingRecords: [record]);
            Check(repeat.Succeeded.Count == 1, "the matching ledger entry permits resending a CUE track");
            byte[] finalSourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            Check(sourceHash.SequenceEqual(finalSourceHash), "the physical image file stays byte-identical");
        }
        finally
        {
            ToolUtils.CoverBytes = null;
            await database.CloseAsync();
            string tempRoot = Path.Combine(AppContext.BaseDirectory, "test-data") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(scratch).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected regression fixture path.");
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void WriteWave(string path, int rate, byte[] samples, short format = 1, short channels = 2, short bits = 16)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length); writer.Write("WAVEfmt "u8); writer.Write(16);
        short blockAlign = (short)(channels * bits / 8);
        writer.Write(format); writer.Write(channels); writer.Write(rate); writer.Write(rate * blockAlign);
        writer.Write(blockAlign); writer.Write(bits); writer.Write("data"u8); writer.Write(samples.Length);
        writer.Write(samples);
    }

    private static byte[] ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            byte[] id = reader.ReadBytes(4);
            int size = reader.ReadInt32();
            if (id.AsSpan().SequenceEqual("data"u8)) return reader.ReadBytes(size);
            reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("WAV data not found.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
