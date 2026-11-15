using System.Security.Cryptography;
using Windows.Storage;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;

int logIndex = Array.IndexOf(args, "--log");
using var log = logIndex >= 0 ? new StreamWriter(args[logIndex + 1]) { AutoFlush = true } : null;
if (log is not null)
{
    Console.SetOut(log);
    Console.SetError(log);
}
try { Console.WriteLine($"Package identity: {Windows.ApplicationModel.Package.Current.Id.FullName}"); }
catch (InvalidOperationException) { Console.WriteLine("Package identity: unpackaged"); }

var devices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
foreach (var device in devices)
    Console.WriteLine($"Device: {device.DisplayName}, Portable: {device.IsPortable}, Free: {device.FreeSpaceInGB} GB");
var phone = devices.FirstOrDefault(d => d.IsPortable);
Check(phone is not null, "The connected MTP phone must be discovered without a drive letter.");
var again = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
Check(again.Any(d => d.UniqueId == phone!.UniqueId), "Repeated enumeration must preserve storage identity.");
Check(string.IsNullOrEmpty(phone!.Path) && phone.DisplayName.Contains('/'), "MTP must display its device/storage name.");
Console.WriteLine("PASS: connected phone discovery, storage identity and display.");
int bulkIndex = Array.IndexOf(args, "--bulk");
if (bulkIndex >= 0)
{
    int count = bulkIndex + 1 < args.Length && int.TryParse(args[bulkIndex + 1], out int requested) ? requested : 128;
    await BulkUsbRegression.RunAsync(phone, count, CreateWave);
    return;
}
if (!args.Contains("--copy")) return;

var local = Directory.CreateTempSubdirectory("OriginalSound-UsbRegression-");
string author = "OriginalSound-UsbRegression-" + Guid.NewGuid().ToString("N");
Console.WriteLine($"Isolated test folder: MUSIC/{author}");
var root = await UsbStorageDeviceReader.OpenPortableStorageAsync(phone);
FFmpeg.AutoGen.ffmpeg.RootPath = AppContext.BaseDirectory;
var converter = new AudioConverterService(new WinUIMusicPlayer.ViewModel.AppViewModel(), WinUIMusicPlayer.App.GetLogger<AudioConverterService>());
var database = new MusicDatabaseService();
var writer = new UsbWriterHelper(converter, database);
try
{
    string databasePath = Path.Combine(local.FullName, "usb.db");
    await database.InitializeAsync(databasePath);
    string source = Path.Combine(local.FullName, "测试音频.wav");
    await File.WriteAllBytesAsync(source, CreateWave());
    var music = new Music(source, author, "测试专辑");
    var result = await writer.WriteToUsb([music, new(Path.Combine(local.FullName, "missing.wav"), author, "测试专辑")], phone);
    Check(result.Succeeded.Count == 1 && result.Succeeded[0].Extension == "wav", "Only a completed audio copy may count as success.");
    await database.SaveUsbExportRecordsAsync(result.Succeeded, phone);
    var musicFolder = await root.GetFolderAsync("MUSIC");
    var authorFolder = await musicFolder.GetFolderAsync(author);
    var album = await authorFolder.GetFolderAsync("测试专辑");
    var copied = await album.GetFileAsync("测试音频.wav");
    Check(await HashAsync(copied) == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))), "Phone audio must match source bytes.");
    Check((await album.GetFilesAsync()).Count == 4, "Audio and three lyric files must all be copied.");
    Console.WriteLine("PASS: real MTP audio bytes, Chinese names, three lyric files and missing source.");

    var stagedBefore = Directory.GetDirectories(Path.GetTempPath(), "OriginalSound-UsbExport-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
    result = await writer.WriteToUsb([music], phone, "invalid");
    Check(result.Succeeded.Count == 1 && result.Succeeded[0].Extension == "wav", "Conversion failure must fall back to the source format.");
    await database.SaveUsbExportRecordsAsync(result.Succeeded, phone);
    result = await writer.WriteToUsb([music], phone, "mp3");
    Check(result.Succeeded.Count == 1 && result.Succeeded[0].Extension == "mp3", "Converted output must use local staging before MTP copy.");
    await database.SaveUsbExportRecordsAsync(result.Succeeded, phone);
    var mp3 = await album.GetFileAsync("测试音频.mp3");
    var mp3Properties = await mp3.Properties.GetMusicPropertiesAsync();
    Console.WriteLine($"MTP properties: title='{mp3Properties.Title}', artist='{mp3Properties.Artist}', album='{mp3Properties.Album}'");
    string roundTrip = Path.Combine(local.FullName, "roundtrip.mp3");
    using (var remoteStream = await mp3.OpenStreamForReadAsync())
    await using (var output = File.Create(roundTrip))
        await remoteStream.CopyToAsync(output);
    var tags = new ATL.Track(roundTrip);
    Check(tags.Title == music.Title && tags.Artist == music.Author && tags.Album == music.Album, "The MP3 downloaded back from the phone must retain real encoded metadata.");
    result = await writer.WriteToUsb([music], phone, "aac");
    Check(result.Succeeded.Count == 1 && result.Succeeded[0].Extension == "m4a", "Real AAC export must use the mapped M4A container extension.");
    await database.SaveUsbExportRecordsAsync(result.Succeeded, phone);
    await album.GetFileAsync("测试音频.m4a");
    Check(Directory.GetDirectories(Path.GetTempPath(), "OriginalSound-UsbExport-*").All(stagedBefore.Contains), "Local conversion staging must be cleaned.");
    Console.WriteLine("PASS: real FFmpeg MP3/AAC encoding, metadata, fallback, overwrite and staging cleanup.");

    Console.WriteLine("Starting production MUSIC scan.");
    var records = await UsbPortableMusicReader.ReadAsync(phone, CancellationToken.None);
    Check(records.Count(r => r.Path.Contains(author)) == 3, "MTP scanner must read WAV, MP3 and M4A using storage objects.");
    await Task.WhenAll(database.ScanPortableUsbDeviceAsync(phone, CancellationToken.None),
        database.SaveUsbExportRecordsAsync(result.Succeeded, phone));
    var stored = (await database.GetUsbDeviceMusics(phone.UniqueId)).Where(r => r.Path.Contains(author)).ToList();
    Check(stored.Count == 3 && stored.All(r => r.Title == music.Title && r.Author == music.Author && r.Album == music.Album),
        "MTP metadata gaps and overlapping snapshot commits must not overwrite confirmed export metadata.");
    await database.CloseAsync();
    await database.InitializeAsync(databasePath);
    stored = (await database.GetUsbDeviceMusics(phone.UniqueId)).Where(r => r.Path.Contains(author)).ToList();
    Check(stored.Count == 3 && stored.Select(r => r.Id).Distinct().Count() == 3 && stored.All(r => r.Title == music.Title),
        "Export records must survive database reopen without duplicate paths.");
    Console.WriteLine("PASS: real SQLite export ledger, MTP metadata gaps, scan/export commits and reopen.");
    var overlappingScan = UsbPortableMusicReader.ReadAsync(phone, CancellationToken.None);
    using var queuedCancellation = new CancellationTokenSource();
    var cancelledExport = writer.WriteToUsb([music], phone, cancellationToken: queuedCancellation.Token);
    queuedCancellation.Cancel();
    Check((await cancelledExport).Succeeded.Count == 0, "A cancelled export waiting for the MTP gate must not write or claim success.");
    var overlappingExport = writer.WriteToUsb([music], phone);
    await Task.WhenAll(overlappingScan, overlappingExport);
    Check(overlappingExport.Result.Succeeded.Count == 1, "Overlapping scan and export must serialize native MTP operations.");
    Console.WriteLine("PASS: overlapping scan and export share the MTP gate.");
    result = await writer.WriteToUsb([music], phone, cancellationToken: new CancellationToken(true));
    Check(result.Succeeded.Count == 0, "Pre-cancelled export must not claim success.");
    try
    {
        await UsbPortableMusicReader.ReadAsync(phone, new CancellationToken(true));
        throw new InvalidOperationException("Cancelled scan completed.");
    }
    catch (OperationCanceledException) { }
    var missingStorage = new UsbStorageDevice { PortableDeviceId = phone.PortableDeviceId, PortableStorageId = "missing", Name = "missing", UniqueId = phone.UniqueId };
    try
    {
        await database.ScanPortableUsbDeviceAsync(missingStorage, CancellationToken.None);
        throw new InvalidOperationException("Unavailable storage scan completed.");
    }
    catch (DirectoryNotFoundException) { }
    Check((await database.GetUsbDeviceMusics(phone.UniqueId)).Count(r => r.Path.Contains(author)) == 3,
        "An incomplete device scan must preserve existing ledger entries.");
    result = await writer.WriteToUsb([music], missingStorage);
    Check(result.Succeeded.Count == 0, "Unavailable storage must not count as success.");
    result = await writer.WriteToUsb([music], phone);
    Check(result.Succeeded.Count == 1, "Failed storage open must release the gate so a later export can retry.");
    await mp3.DeleteAsync(StorageDeleteOption.PermanentDelete);
    await database.ScanPortableUsbDeviceAsync(phone, CancellationToken.None);
    Check((await database.GetUsbDeviceMusics(phone.UniqueId)).Count(r => r.Path.Contains(author)) == 2,
        "A complete scan must remove records for deleted files.");
    Console.WriteLine("PASS: real MTP scan, cancellation and unavailable storage.");
}
finally
{
    try
    {
        var musicFolder = await root.GetFolderAsync("MUSIC");
        var testFolder = await musicFolder.GetFolderAsync(author);
        Check(testFolder.Name == author && author.StartsWith("OriginalSound-UsbRegression-", StringComparison.Ordinal), "Unsafe phone cleanup target.");
        foreach (var album in await testFolder.GetFoldersAsync())
        {
            foreach (var file in await album.GetFilesAsync()) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await album.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        await testFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
    }
    catch (FileNotFoundException) { }
    finally
    {
        await database.CloseAsync();
        Check(local.FullName.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "Unsafe local cleanup target.");
        local.Delete(true);
    }
    Console.WriteLine("Cleaned isolated phone and local test folders.");
}
Console.WriteLine("PASS: all USB integration checks completed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<string> HashAsync(StorageFile file)
{
    using var stream = await file.OpenStreamForReadAsync();
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

static byte[] CreateWave()
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(36 + 88200);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)2);
    writer.Write(44100);
    writer.Write(176400);
    writer.Write((short)4);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(88200);
    writer.Write(new byte[88200]);
    return stream.ToArray();
}
