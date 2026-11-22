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

    private static void WriteWave(string path, int rate, byte[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
        writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples.Length);
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
