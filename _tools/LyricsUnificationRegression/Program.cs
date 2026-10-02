using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;
using SQLite;

var parser = new LyricsParser();
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
    checks++;
}

LineEndingChecks.Run(parser, Check);

var lrc = parser.Import("[00:01.00][00:03.000]Hello\n[00:04.00]World", "[00:01.000]译文");
var parsed = parser.Parse(lrc, 6000);
Check(parsed.Length == 3 && parsed[0].Translation == "译文" && parsed[1].StartMs == 3000, "LRC repeated timestamps and separate translation");
var pronunciation = parser.Import("[00:01.000]Hello\n[00:02.000]World", pronunciation: "[00:01.000]he-lo\n[00:02.000]wurld");
var pronunciationLines = parser.Parse(pronunciation, 5000);
Check(pronunciation.PronunciationLrc is not null && pronunciationLines[0].Pronunciation == "he-lo" && pronunciationLines[1].Pronunciation == "wurld",
    "independent pronunciation LRC is normalized and timestamp-aligned");
var qrc = parser.Import("[1000,1000]你(1000,400)好(1400,600)", "[1000,1000]译(1000,500)文(1500,500)");
Check(qrc.Original.Format == LyricsFormat.Qrc && qrc.TranslationLrc == "[00:01.000]译文\n", "historical Krc slot syntax is QRC; translation becomes LRC");
var krc = parser.Import("[1000,1000]<0,400,0>你<400,600,0>好");
Check(parser.Parse(krc, 5000)[0].Words[1].StartMs == 1400, "KRC relative word timestamps");
var enhanced = parser.Import("[00:01.000]<00:01.000>你<00:01.500>好<00:02.000>");
Check(parser.Parse(enhanced, 5000)[0].Words.Length == 2, "enhanced LRC remains word timed");
const string ttml = """
<tt xmlns="http://www.w3.org/ns/ttml" xmlns:i="http://music.apple.com/lyric-ttml-internal"><head><metadata><i:translations><i:translation type="subtitle" xml:lang="en-US"><i:text for="L1">Translated</i:text></i:translation></i:translations></metadata></head><body><div><p begin="1.000" end="3.000" i:key="L1"><span begin="1.000" end="2.000">Hello</span> <span begin="2.000" end="3.000">world</span></p><p begin="2.500" end="4.000">Overlap</p></div></body></tt>
""";
var xmlDoc = parser.Import(ttml);
var xmlLines = parser.Parse(xmlDoc, 6000);
Check(xmlDoc.TranslationLrc == "[00:01.000]Translated\n" && !xmlDoc.Original.Content.Contains("<i:translations>"), "TTML split into original and independent LRC");
Check(xmlLines[0].EndMs == 3000 && xmlLines[1].StartMs == 2500 && xmlLines[0].Words.Length == 2, "TTML preserves explicit end and overlapping line");
const string ttmlWithPronunciationText = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:i=\"http://music.apple.com/lyric-ttml-internal\"><head><metadata><i:transliterations><i:transliteration xml:lang=\"ja-Latn\"><i:text for=\"L1\">konnichi wa</i:text></i:transliteration></i:transliterations></metadata></head><body><div><p begin=\"1.000\" end=\"2.000\" i:key=\"L1\">こんにちは</p></div></body></tt>";
var ttmlWithPronunciation = parser.Import(ttmlWithPronunciationText, preferredLanguage: "en-US");
Check(ttmlWithPronunciation.PronunciationLrc?.Contains("konnichi wa", StringComparison.Ordinal) == true,
    "TTML transliteration metadata imports into pronunciation LRC");
Check(LyricsFilePolicy.NormalizeOrder("ttml,lrc,ttml,bad") == "ttml,lrc,krc,qrc", "priority validation and new formats");
var old = new MusicLyrics { MusicId = 1, Lyrics = "[00:01.000]old", TranslatedLyrics = "[00:01.000]old translation", Krc = qrc.Original.Content, TKrc = qrc.TranslationLrc! };
var migrated = LyricsLegacyMigration.Convert(old, parser);
Check(migrated.Lyrics == old.Krc && migrated.TranslatedLyrics == old.TKrc, "legacy pairs stay together");
old.Krc = "broken";
Check(LyricsLegacyMigration.Convert(old, parser).Lyrics == old.Lyrics, "invalid preferred original falls back");
old.Krc = qrc.Original.Content; old.TKrc = "untimed";
Check(LyricsLegacyMigration.Convert(old, parser).Diagnostic.Length > 0, "failed translation normalization is diagnosed");


string folder = Path.Combine(Path.GetTempPath(), "lyrics-v2-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);

var tagLyrics = new ATL.LyricsInfo();
tagLyrics.Parse(parser.ExportLrc(parser.Import("[00:01.234]First\n[00:02.567]Second")));
Check(tagLyrics.SynchronizedLyrics.Count == 2 && tagLyrics.SynchronizedLyrics[0].TimestampStart == 1234,
    "ATL accepts complete normalized LRC with millisecond precision");
string audioFixture = Path.Combine(folder, "tag-roundtrip.wav");
using (var writer = new BinaryWriter(File.Create(audioFixture)))
{
    writer.Write("RIFF"u8); writer.Write(36 + 8820); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(8820); writer.Write(new byte[8820]);
}
var tagged = new ATL.Track(audioFixture) { Lyrics = [tagLyrics] };
Check(tagged.Save() && new ATL.Track(audioFixture).Lyrics.Any(info => info.SynchronizedLyrics.Count == 2), "real WAV tag write and reread retains both original LRC lines");
var db = new SQLiteAsyncConnection(Path.Combine(folder, "test.db"));
await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY)");
await db.ExecuteAsync("INSERT INTO Music(Id) VALUES(1),(2)");
await db.CreateTableAsync<MusicLyrics>();
await db.InsertAsync(old);
var repository = new LyricsRepository(db, parser);
await repository.InitializeAsync(Path.Combine(folder, "test.db"));
Check(File.Exists(Path.Combine(folder, "test.db.lyrics-v1.bak")), "consistent migration backup created");
var snapshots = await Task.WhenAll(repository.GetAsync(1), repository.GetAsync(1));
Check(snapshots[0].Revision == 1 && await db.Table<MusicLyricsRecord>().CountAsync() == 1, "concurrent lazy migration is idempotent");
Check(await repository.SaveAsync(1, LyricsDocument.Empty, 1, "User"), "user can explicitly clear the active document");
Check(!await repository.SaveAsync(1, qrc, 1, "Online"), "late download cannot overwrite an edit");
Check((await repository.GetAsync(1)).Document == LyricsDocument.Empty, "cleared row does not resurrect legacy content");
Check((await db.FindAsync<MusicLyrics>(1)).TKrc == "untimed", "all original recovery fields remain unchanged");
await db.RunInTransactionAsync(connection => LyricsRepository.DeleteInTransaction(connection, 1));
Check(await db.FindAsync<MusicLyricsRecord>(1) is null && await db.FindAsync<MusicLyrics>(1) is null, "delete removes active and legacy lyric rows");

await db.InsertAsync(new MusicLyrics { MusicId = 2, Lyrics = "[00:01.00]legacy" });
await repository.MigrateRemainingAsync(default);
var second = await repository.GetAsync(2);
await repository.SaveAsync(2, lrc, second.Revision, "User");
await db.ExecuteAsync("UPDATE MusicLyrics SET Lyrics='changed by old version' WHERE MusicId=2");
var downgrade = await repository.GetAsync(2);
Check(downgrade.Document == lrc && downgrade.Diagnostic == "LegacyChangedAfterUpgrade", "downgrade conflict preserves new edit and reports diagnostic");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await repository.MigrateRemainingAsync(cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
await repository.MigrateRemainingAsync(default);
Check(await db.Table<MusicLyricsRecord>().CountAsync() == 1, "background migration resumes without reviving deleted content");

string cacheDir = Path.Combine(folder, "cache");
Directory.CreateDirectory(cacheDir);
string musicPath = Path.Combine(folder, "Song.flac");
string cacheFile = Path.Combine(cacheDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(musicPath.ToUpperInvariant()))) + ".json");
string legacyJson = "{\"Path\":" + "\"" + musicPath.Replace("\\", "\\\\") + "\"" + ",\"Krc\":\"[1000,500]word(1000,500)\",\"Lrc\":\"[00:01.00]other\"}";
await File.WriteAllTextAsync(cacheFile, legacyJson);
var cache = new LyricsCacheStore(cacheDir, parser);
var cacheEntry = cache.Load(musicPath);
Check(cacheEntry?.Document.Original.Format == LyricsFormat.Qrc && File.ReadAllText(Path.Combine(cacheDir, "legacy", Path.GetFileName(cacheFile))) == legacyJson, "legacy JSON migration keeps whole pairs and exact recovery copy");
cache.Save(musicPath, xmlDoc);
Check(cache.Load(musicPath)?.Document == xmlDoc, "reflection-disabled JSON round trip for unified document");
var pronunciationDocument = xmlDoc with { PronunciationLrc = "[00:01.000]Romanized\n" };
cache.Save(musicPath, pronunciationDocument);
Check(cache.Load(musicPath)?.Document == pronunciationDocument, "JSON round trip preserves pronunciation LRC");
cache.Save(musicPath, xmlDoc);
long cacheRevision = cache.Load(musicPath)!.Revision;
Check(cache.Save(musicPath, lrc, cacheRevision), "external editor conditionally saves the observed cache revision");
Check(!cache.Save(musicPath, xmlDoc, cacheRevision) && cache.Load(musicPath)?.Document == lrc, "late external download cannot overwrite a cache edit");
Check(cache.Save(musicPath, LyricsDocument.Empty, cacheRevision + 1, sourceKind: "User"), "explicit cache clear is recorded as a user edit");
var reopenedCache = new LyricsCacheStore(cacheDir, parser);
Check(reopenedCache.Load(musicPath) is { SourceKind: "User", Document.Original.Content: "" }, "user clear survives JSON persistence and a new cache instance");
Check(!cache.Save(musicPath, qrc, cacheRevision + 1) && reopenedCache.Load(musicPath)?.SourceKind == "User", "late download cannot change cleared cache provenance");
string beforeSourceKind = File.ReadAllText(cacheFile).Replace(",\"SourceKind\":\"User\"", "");
await File.WriteAllTextAsync(cacheFile, beforeSourceKind);
Check(reopenedCache.Load(musicPath) is { SourceKind: "", Document.Original.Content: "" }, "older V2 JSON without provenance remains readable");
await File.WriteAllTextAsync(cacheFile, "{\"SchemaVersion\":99}");
cache.Save(musicPath, lrc);
Check(cache.Load(musicPath) is null && File.ReadAllText(cacheFile).Contains("99"), "unknown future cache version preserved");
await File.WriteAllTextAsync(cacheFile, "broken json");
cache.Save(musicPath, lrc);
Check(cache.Load(musicPath)?.Document == lrc, "corrupt cache can recover on successful search");
await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => cache.Save(musicPath, i % 2 == 0 ? lrc : xmlDoc))));
Check(cache.Load(musicPath) is not null && !Directory.EnumerateFiles(cacheDir, "*.tmp").Any(), "concurrent cache writes are complete and leave no temporary files");

string exported = await LyricsExporter.SaveFilesAsync(musicPath, xmlDoc, default);
Check(exported.EndsWith(".ttml") && File.ReadAllText(exported) == xmlDoc.Original.Content && File.Exists(Path.Combine(folder, "Song_Translated.lrc")), "export preserves TTML and uses separate LRC translation");
string translationPath = Path.Combine(folder, "Song_Translated.lrc");
using (var held = new FileStream(translationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { await LyricsExporter.SaveFilesAsync(musicPath, xmlDoc with { Original = new(ttml + " ", LyricsFormat.Ttml) }, default); throw new Exception("Expected export failure"); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}
Check(File.ReadAllText(exported) == xmlDoc.Original.Content && File.ReadAllText(translationPath) == xmlDoc.TranslationLrc, "second-file export failure rolls back the original and retains recovery backup");
await LyricsExporter.SaveFilesAsync(musicPath, xmlDoc with { TranslationLrc = null }, default);
Check(!File.Exists(translationPath) && File.Exists(translationPath + ".lyrics.bak"), "empty translation removes stale output with recoverable backup");
Check(parser.Parse(parser.Import("[00:01.000]A\n[00:01.000]B", "[00:01.000]T"), 3000).All(line => line.Translation == ""), "duplicate timestamps do not infer or cross-pair tracks");
Check(parser.Parse(parser.Import("[00:01.000]A\n[00:01.200]B", "[00:01.100]T"), 3000).All(line => line.Translation == ""), "ambiguous near-match leaves translation unattached");
Check(parser.Parse(parser.Import("[00:01.000]A<00:01.500>B"), 3000)[0].Words[^1].EndMs == 1500, "enhanced LRC without a closing timestamp preserves the legacy instant final word");
Check(!parser.HasLyrics(new(new("<!DOCTYPE tt [<!ENTITY e SYSTEM 'file:///secret'>]><tt xmlns='http://www.w3.org/ns/ttml'><body><p begin='1'> &e; </p></body></tt>", LyricsFormat.Ttml), null)), "external XML entities rejected");
string samples = Path.Combine(Path.GetTempPath(), "music-player-lyrics-issue27");
foreach (var sample in new[] { ("32505618.ttml", 0), ("32552336.ttml", 450) })
{
    string file = Path.Combine(samples, sample.Item1);
    if (!File.Exists(file)) { Console.WriteLine("SKIP external issue sample: " + file); continue; }
    var imported = parser.Import(await File.ReadAllTextAsync(file), preferredLanguage: "de-DE");
    var lines = parser.Parse(imported, 300000);
    Check(lines.Length == 27 && lines.Count(line => line.Translation.Length > 0) == 27 && lines.Sum(line => line.Words.Length) == sample.Item2,
        $"issue #27 {sample.Item1}: 27 original + 27 translations + {sample.Item2} words in non-Chinese locale");
    Check(parser.Parse(parser.Import(imported.Original.Content, imported.TranslationLrc), 300000).Length == 27, "split TTML can be saved and reimported");
}

var online = new LyricsOnlineSearch { Handler = (_, _, _) => Task.FromResult<LyricsDocument?>(qrc) };
var resolver = new WinUIMusicPlayer.Services.LyricsRefreshService(new(db, parser), parser, online, new(),
    Microsoft.Extensions.Logging.Abstractions.NullLogger<WinUIMusicPlayer.Services.LyricsRefreshService>.Instance);
await LineEndingChecks.CheckPlaybackAsync(resolver, repository, db, folder, Check);
await EmptyTimestampChecks.RunAsync(parser, resolver, folder, Check);
await PlaybackCompatibilityChecks.RunAsync(parser, resolver, folder, Check);
await HighlightTimingChecks.RunAsync(parser, resolver, repository, db, folder, Check);
string sourcePath = Path.Combine(folder, "Source.flac");
await File.WriteAllTextAsync(Path.ChangeExtension(sourcePath, ".krc"), "bad");
await File.WriteAllTextAsync(Path.ChangeExtension(sourcePath, ".lrc"), "[00:01.00]local");
await File.WriteAllTextAsync(Path.Combine(folder, "Source_Translated.lrc"), "[00:01.00]translation");
var external = new WinUIMusicPlayer.Model.Music { Path = sourcePath };
var displayed = await resolver.SetLyrics(external, default);
Check(displayed.Count == 1 && displayed[0].TransLateText == "translation" && online.Calls == 0, "actual file resolution falls back from broken KRC and matches independent LRC translation");
await File.WriteAllTextAsync(Path.ChangeExtension(sourcePath, ".ttml"), ttml);
WinUIMusicPlayer.Model.AppSettings.LocalLyricsFormatOrder = "ttml,lrc,krc,qrc";
var newDisplay = await resolver.SetLyrics(external, default);
Check(newDisplay.Count == 2 && newDisplay[0].EndMs == 3000 && newDisplay[0].TransLateText == "translation", "TTML priority and explicit sidecar translation override embedded metadata");
Check(displayed[0].Words.Count > 0 && displayed[0].Words[0].Word == "local", "new publication never clears words retained by old UI snapshot");
await repository.SaveAsync(2, LyricsDocument.Empty, (await repository.GetAsync(2)).Revision, "User");
await resolver.SetLyrics(new() { Id = 2, Path = Path.Combine(folder, "cleared.flac") }, default);
Check(online.Calls == 0, "explicit user clear suppresses automatic refetch");
await resolver.SetLyrics(new() { Path = Path.Combine(folder, "A.flac") }, default);
await resolver.SetLyrics(new() { Path = Path.Combine(folder, "B.flac") }, default);
await resolver.SetLyrics(new() { Path = Path.Combine(folder, "A.flac") }, default);
Check(online.Calls == 2 && await db.Table<MusicLyricsRecord>().CountAsync() == 1, "two external paths have independent caches and never write MusicId zero");
string emptyAutomaticPath = Path.Combine(folder, "empty-automatic.flac");
WinUIMusicPlayer.Services.OneShotLyricsCache.Save(emptyAutomaticPath, LyricsDocument.Empty);
await resolver.SetLyrics(new() { Path = emptyAutomaticPath }, default);
Check(online.Calls == 3, "empty automatic cache does not suppress a legitimate search");
await SourcePriorityChecks.RunAsync(parser, Check);
await db.CloseAsync();
Console.WriteLine($"{checks} checks passed; SQLite artifacts: {folder}");
