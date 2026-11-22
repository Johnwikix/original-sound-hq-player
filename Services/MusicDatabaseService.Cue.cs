using Microsoft.Extensions.Logging;
using SQLite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private static StringComparer CuePathComparer => StringComparer.OrdinalIgnoreCase;
    private static string CuePathKey(string path) => path.ToUpperInvariant();

    public Task<Music?> FindCueTrackAsync(string cuePath, int trackNo) => _dbConnection.FindWithQueryAsync<Music>(
        "SELECT m.* FROM Music m JOIN CueSheet s ON s.Id=m.CueSheetId WHERE s.PathKey=? AND m.CueTrackNo=? AND m.CueMappingActive=1 AND m.IsDetached=0",
        CuePathKey(Path.GetFullPath(cuePath)), trackNo)!;

    public Task<List<Music>> GetMusicByPhysicalPathAsync(string path) => _dbConnection.QueryAsync<Music>(
        "SELECT * FROM Music WHERE Path=? COLLATE NOCASE ORDER BY Id", Path.GetFullPath(path));

    internal async Task<bool> HasActiveCueSourceAsync(string path) => await _dbConnection.ExecuteScalarAsync<int>(
        ActiveCueSourceSql, path) != 0;

    public async Task<List<Music>> ImportCueAsync(string cuePath, CancellationToken token = default)
    {
        using var lease = await LibraryOperationGate.EnterAsync(token, reportBusy: false);
        cuePath = Path.GetFullPath(cuePath);
        string directory = Path.GetDirectoryName(cuePath)!;
        var inputs = AddFolderService.EnumerateScanDirectories(directory, recursive: false).Single();
        if (!inputs.Cues.Contains(cuePath, CuePathComparer)) return [];
        var result = await ReconcileCueDirectoryAsync(inputs, token);
        var tracks = await _dbConnection.QueryAsync<Music>(
            "SELECT m.* FROM Music m JOIN CueSheet s ON s.Id=m.CueSheetId WHERE s.PathKey=? AND m.CueMappingActive=1 AND m.IsMissing=0 AND m.IsDetached=0 ORDER BY m.CueTrackNo,m.Id",
            CuePathKey(cuePath));
        if (tracks.Count == 0)
        {
            string? canonical = result.Aliases.GetValueOrDefault(cuePath);
            if (canonical is null) return [];
            tracks = await _dbConnection.QueryAsync<Music>(
                "SELECT m.* FROM Music m JOIN CueSheet s ON s.Id=m.CueSheetId WHERE s.PathKey=? AND m.CueMappingActive=1 AND m.IsMissing=0 AND m.IsDetached=0 ORDER BY m.CueTrackNo,m.Id",
                CuePathKey(canonical));
        }
        if (tracks.Count > 0 && !(await GetFolders()).Any(folder => !folder.IsExternalImport && LibraryPath.IsWithin(directory, folder.Path)))
            await EnsureExternalFolderAsync();
        if (Versions is not null) await Versions.DecorateAsync(tracks);
        return tracks;
    }

    private sealed record CueDirectoryResult(HashSet<string> CoveredPaths, Dictionary<string, string> Aliases, int Changes);
    private sealed record WholeRestoration(CueFileRevision Revision, Music Music, LyricsDocument? Lyrics);
    private sealed record CueCandidate(string Path, CueSheetRecord? Prior, CueSheetReader? Sheet, Music[] Mapping)
    {
        public IEnumerable<string> Paths => Mapping.Select(track => track.Path);
    }

    private async Task<CueDirectoryResult> ReconcileCueDirectoryAsync(ScanDirectory inputs, CancellationToken token,
        string? previousDirectory = null, IReadOnlyDictionary<string, string>? renames = null,
        Func<IReadOnlyList<Music>, Task>? onBatchInserted = null, Action? onCommitted = null,
        IReadOnlySet<string>? restrictedAudioPaths = null)
    {
        token.ThrowIfCancellationRequested();
        string key = CuePathKey(inputs.Path);
        var stored = await _dbConnection.QueryAsync<CueSheetRecord>(
            "SELECT Id,Path,Fingerprint,IsActive FROM CueSheet WHERE DirectoryKey=? OR DirectoryKey=?",
            key, previousDirectory is null ? key : CuePathKey(previousDirectory));
        if (renames is not null)
            foreach (var (oldPath, newPath) in renames)
            {
                if (!AddFolderService.IsCueFile(oldPath) || !inputs.Cues.Contains(newPath, CuePathComparer) ||
                    stored.Any(sheet => CuePathComparer.Equals(sheet.Path, oldPath))) continue;
                var old = await _dbConnection.FindWithQueryAsync<CueSheetRecord>(
                    "SELECT Id,Path,Fingerprint,IsActive FROM CueSheet WHERE PathKey=?", CuePathKey(oldPath));
                if (old is not null) stored.Add(old);
            }
        var sources = new Dictionary<int, List<CueSourceRecord>>();
        var currentRows = new Dictionary<int, Music[]>();
        if (stored.Count > 0)
        {
            string ids = string.Join(',', stored.Select(sheet => sheet.Id));
            foreach (var source in await _dbConnection.QueryAsync<CueSourceRecord>($"SELECT * FROM CueSource WHERE CueSheetId IN ({ids})"))
            {
                if (!sources.TryGetValue(source.CueSheetId, out var list)) sources.Add(source.CueSheetId, list = []);
                list.Add(source);
            }
            var rows = await _dbConnection.QueryAsync<Music>(
                $"SELECT Path,CueSheetId,CueTrackNo,StartSample,EndSample,IsMissing,IsDetached FROM Music WHERE CueSheetId IN ({ids}) AND CueMappingActive=1 ORDER BY CueTrackNo");
            foreach (var group in rows.GroupBy(row => row.CueSheetId!.Value)) currentRows.Add(group.Key, group.ToArray());
        }

        var candidates = new List<CueCandidate>(inputs.Cues.Count);
        var presentPaths = new HashSet<string>(inputs.Cues, CuePathComparer);
        var kept = new HashSet<int>();
        var used = new HashSet<int>();
        var cache = new Dictionary<string, CueInput>(CuePathComparer);
        bool attemptedPending = false;
        foreach (string path in inputs.Cues.Order(CuePathComparer).ThenBy(path => path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var prior = stored.FirstOrDefault(sheet => CuePathComparer.Equals(sheet.Path, path));
            prior ??= FindRenamedCue(path);
            if (prior is not null) kept.Add(prior.Id);
            try
            {
                var text = await CueSheetReader.ReadTextAsync(path, token);
                if (prior is null)
                {
                    var matching = stored.Where(sheet => !presentPaths.Contains(sheet.Path) && !used.Contains(sheet.Id) && sheet.Fingerprint == text.Fingerprint).Take(2).ToArray();
                    if (matching.Length == 1 && LibraryPath.IsConfirmedMissing(matching[0].Path, inputs.Path))
                    {
                        prior = matching[0];
                        kept.Add(prior.Id);
                    }
                }
                if (prior is not null && used.Contains(prior.Id)) prior = null;
                if (prior is { IsActive: true } && prior.Path == path && prior.Fingerprint == text.Fingerprint &&
                    currentRows.TryGetValue(prior.Id, out var rows) && rows.All(row => !row.IsMissing && !row.IsDetached) &&
                    sources.TryGetValue(prior.Id, out var dependencies) && dependencies.All(SourceUnchanged))
                {
                    candidates.Add(new(path, prior, null, rows));
                    used.Add(prior.Id);
                    continue;
                }

                var parsed = await CueSheetReader.ReadAsync(text, cache, token);
                if (restrictedAudioPaths is not null && parsed.Sources.Any(source => !restrictedAudioPaths.Contains(source.Path))) continue;
                if (!attemptedPending && await HasPendingCueWritesAsync(parsed))
                {
                    attemptedPending = true;
                    await FlushMetadataWritesAsync(token);
                    if (!parsed.IsCurrent())
                    {
                        cache.Clear();
                        parsed = await CueSheetReader.ReadAsync(await CueSheetReader.ReadTextAsync(path, token), cache, token);
                    }
                }
                if (parsed.EncodingName is not ("UTF-8" or "UTF-8 BOM"))
                    _logger.LogInformation("CUE 编码 {Encoding}: {Path}", parsed.EncodingName, path);
                if (prior is not null) used.Add(prior.Id);
                candidates.Add(new(path, prior, parsed, parsed.Tracks.OrderBy(track => track.CueTrackNo).ToArray()));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                _logger.LogWarning(ex, "无法验证 CUE 清单: {Path}", path);
            }
        }

        var retire = new List<CueSheetRecord>();
        var covered = new HashSet<string>(CuePathComparer);
        foreach (var old in stored)
        {
            if (!old.IsActive || used.Contains(old.Id)) continue;
            bool movePending = renames?.TryGetValue(old.Path, out var movedTo) == true && AddFolderService.IsCueFile(movedTo);
            if (kept.Contains(old.Id) || movePending || !LibraryPath.IsConfirmedMissing(old.Path, previousDirectory ?? inputs.Path))
                KeepOldSources(old.Id);
            else retire.Add(old);
        }

        var aliases = new Dictionary<string, string>(CuePathComparer);
        var accepted = new List<CueCandidate>();
        // An already verified mapping wins over new catalogs that claim any of its sources.
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Prior?.IsActive == true))
        {
            var overlaps = candidates.Where(other => !ReferenceEquals(other, candidate) &&
                other.Paths.Intersect(candidate.Paths, CuePathComparer).Any()).ToArray();
            bool hasConflict = overlaps.Any(other => !SameMapping(candidate, other));
            if (candidate.Paths.Any(covered.Contains) || hasConflict && (candidate.Prior?.IsActive != true || overlaps.Any(other => other.Prior?.IsActive == true)))
            {
                var same = accepted.FirstOrDefault(other => SameMapping(candidate, other));
                if (same is not null) aliases.Add(candidate.Path, same.Path);
                else _logger.LogWarning("CUE 清单引用了冲突的音源: {Path}", candidate.Path);
                if (candidate.Prior?.IsActive == true) KeepOldSources(candidate.Prior.Id);
                continue;
            }
            covered.UnionWith(candidate.Paths);
            accepted.Add(candidate);
        }

        var added = new List<Music>();
        var restorations = new Dictionary<string, WholeRestoration>(CuePathComparer);
        int changed = 0;
        if (retire.Count > 0 || accepted.Any(candidate => candidate.Sheet is not null))
        {
            var releaseIds = retire.Select(sheet => sheet.Id).Concat(accepted
                .Where(candidate => candidate.Sheet is not null && candidate.Prior?.IsActive == true)
                .Select(candidate => candidate.Prior!.Id));
            foreach (string path in releaseIds.SelectMany(id => sources.GetValueOrDefault(id) ?? [])
                .Select(source => source.Path).Distinct(CuePathComparer))
            {
                if (covered.Contains(path) || !File.Exists(path)) continue;
                if (await PrepareWholeRestorationAsync(path, token) is { } restoration) restorations.Add(path, restoration);
            }

            await _metadataGate.WaitAsync(token);
            try
            {
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    token.ThrowIfCancellationRequested();
                    var reviewContainers = new HashSet<string>(CuePathComparer);
                    var valid = new List<CueCandidate>();
                    foreach (var item in accepted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Sheet is not { } sheet) continue;
                        if (sheet.IsCurrent() && !sheet.Sources.Any(source => HasPendingCueWrite(db, source.Path))) valid.Add(item);
                        else
                        {
                            covered.ExceptWith(item.Paths);
                            if (item.Prior?.IsActive == true) KeepOldSources(item.Prior.Id);
                        }
                    }
                    foreach (var old in retire)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!LibraryPath.IsConfirmedMissing(old.Path, previousDirectory ?? inputs.Path) || !CanReleaseSources(db, old.Id))
                        {
                            KeepOldSources(old.Id);
                            continue;
                        }
                        changed += RetireCue(db, old.Id);
                        if (sources.TryGetValue(old.Id, out var oldSources)) reviewContainers.UnionWith(oldSources.Select(input => input.Path));
                    }
                    foreach (var item in valid)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.Prior?.IsActive == true && !CanReleaseSources(db, item.Prior.Id))
                        {
                            covered.ExceptWith(item.Paths);
                            KeepOldSources(item.Prior.Id);
                            continue;
                        }
                        var sheet = item.Sheet!;
                        if (item.Prior is not null && sources.TryGetValue(item.Prior.Id, out var priorSources))
                            reviewContainers.UnionWith(priorSources.Select(source => source.Path));
                        foreach (var source in sheet.Sources) reviewContainers.Add(source.Path);
                        changed += CommitCueSheet(db, sheet, item.Prior, sources.GetValueOrDefault(item.Prior?.Id ?? 0), renames, added);
                    }
                    foreach (string path in reviewContainers)
                    {
                        int claimed = db.ExecuteScalar<int>(ActiveCueSourceSql, path);
                        changed += db.Execute("""
                            UPDATE Music SET IsCueContainer=?
                            WHERE SourceId=0 AND Path=? COLLATE NOCASE AND COALESCE(CueTrackNo,0)=0
                              AND COALESCE(IsCueContainer,0)<>?
                            """, claimed, path, claimed);
                        if (claimed != 0 || !restorations.TryGetValue(path, out var restoration) || db.ExecuteScalar<int>(HasWholeVersionSql, path) != 0)
                            continue;
                        var whole = restoration.Music;
                        db.Insert(whole);
                        MusicMetadataStorage.AttachSong(db, whole);
                        if (restoration.Lyrics is { } lyrics) LyricsRepository.AddEmbeddedInTransaction(db, whole.Id, lyrics);
                        added.Add(whole);
                        covered.Add(path);
                        changed++;
                    }
                    token.ThrowIfCancellationRequested();
                });
            }
            finally { _metadataGate.Release(); }
        }
        if (changed > 0)
        {
            onCommitted?.Invoke();
            changed += await ApplyAutomaticSongLinksAsync(added, token);
            if (added.Count > 0 && onBatchInserted is not null) await onBatchInserted(added);
        }
        return new(covered, aliases, changed);

        void KeepOldSources(int sheetId)
        {
            if (sources.TryGetValue(sheetId, out var oldSources)) covered.UnionWith(oldSources.Select(source => source.Path));
        }

        CueSheetRecord? FindRenamedCue(string current)
        {
            string? old = renames?.FirstOrDefault(pair => CuePathComparer.Equals(pair.Value, current)).Key;
            if (old is null && previousDirectory is not null)
                old = Path.Combine(previousDirectory, Path.GetFileName(current));
            if (old is null || presentPaths.Contains(old)) return null;
            return stored.FirstOrDefault(sheet => CuePathComparer.Equals(sheet.Path, old));
        }

        static bool SourceUnchanged(CueSourceRecord source) =>
            new CueFileRevision(source.Length, source.LastWriteUtcTicks).Matches(source.Path) && !AudioFileWriteGate.IsBeingWritten(source.Path);

        bool CanReleaseSources(SQLiteConnection db, int sheetId)
        {
            if (!sources.TryGetValue(sheetId, out var original)) return true;
            foreach (var source in original)
            {
                string path = source.Path;
                if (covered.Contains(path) || db.ExecuteScalar<int>(HasWholeVersionSql, path) != 0 ||
                    LibraryPath.IsConfirmedMissing(path, Path.GetDirectoryName(path)!)) continue;
                if (!restorations.TryGetValue(path, out var restoration) || !restoration.Revision.Matches(path)) return false;
            }
            return true;
        }
    }

    private const string ActiveCueSourceSql = "SELECT EXISTS(SELECT 1 FROM CueSource c JOIN CueSheet s ON s.Id=c.CueSheetId WHERE c.Path=? COLLATE NOCASE AND s.IsActive=1)";
    private const string HasWholeVersionSql = "SELECT EXISTS(SELECT 1 FROM Music WHERE SourceId=0 AND Path=? COLLATE NOCASE AND (COALESCE(CueTrackNo,0)=0 OR CueReusedWhole=1))";

    private async Task<WholeRestoration?> PrepareWholeRestorationAsync(string path, CancellationToken token)
    {
        if (await _dbConnection.ExecuteScalarAsync<int>(HasWholeVersionSql, path) != 0) return null;
        try
        {
            var revision = CueFileRevision.Read(path);
            var (music, lyrics) = await AddFolderService.ReadMusicAsync(path, token);
            if (music is null || !revision.Matches(path)) return null;
            LyricsDocument? document = null;
            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                try { document = new LyricsParser().Import(lyrics); }
                catch (Exception ex) when (ex is FormatException or OverflowException or System.Xml.XmlException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                { _logger.LogWarning(ex, "Embedded lyrics could not be imported: {Path}", path); }
            }
            return new(revision, music, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "无法恢复 CUE 整轨版本: {Path}", path);
            return null;
        }
    }

    private static bool SameMapping(CueCandidate left, CueCandidate right)
    {
        if (left.Mapping.Length != right.Mapping.Length) return false;
        for (int i = 0; i < left.Mapping.Length; i++)
        {
            var a = left.Mapping[i];
            var b = right.Mapping[i];
            if (a.CueTrackNo != b.CueTrackNo || a.StartSample != b.StartSample || a.EndSample != b.EndSample || !CuePathComparer.Equals(a.Path, b.Path)) return false;
        }
        return true;
    }

    private async Task<bool> HasPendingCueWritesAsync(CueSheetReader sheet)
    {
        foreach (var source in sheet.Sources)
            if (await _dbConnection.ExecuteScalarAsync<int>("SELECT EXISTS(SELECT 1 FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE)", source.Path) != 0) return true;
        return false;
    }

    private static bool HasPendingCueWrite(SQLiteConnection db, string path) =>
        db.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE)", path) != 0;

    private static int RetireCue(SQLiteConnection db, int id)
    {
        db.Execute("UPDATE CueSheet SET IsActive=0 WHERE Id=?", id);
        RestoreWholeVersions(db, id);
        return db.Execute("UPDATE Music SET IsMissing=1,CueMappingActive=0,MetadataRevision=MetadataRevision+1 WHERE CueSheetId=? AND CueMappingActive=1", id) + 1;
    }

    private static void RestoreWholeVersions(SQLiteConnection db, int id) => db.Execute("""
        UPDATE Music SET CueSheetId=NULL,CueTrackNo=0,CuePath='',CueSourceTitle='',StartSample=0,EndSample=0,
            CueMappingActive=0,CueReusedWhole=0,UpdateTime=0,MetadataRevision=MetadataRevision+1
        WHERE CueSheetId=? AND CueMappingActive=1 AND CueReusedWhole=1
        """, id);

    private static int CommitCueSheet(SQLiteConnection db, CueSheetReader sheet, CueSheetRecord? prior,
        List<CueSourceRecord>? priorSources, IReadOnlyDictionary<string, string>? renames, List<Music> added)
    {
        int sheetId;
        string directory = Path.GetDirectoryName(sheet.Path)!;
        if (prior is null)
        {
            db.Execute("INSERT INTO CueSheet(Path,PathKey,DirectoryKey,Fingerprint,IsActive) VALUES(?,?,?,?,1)",
                sheet.Path, CuePathKey(sheet.Path), CuePathKey(directory), sheet.Fingerprint);
            sheetId = db.ExecuteScalar<int>("SELECT last_insert_rowid()");
        }
        else
        {
            sheetId = prior.Id;
            db.Execute("UPDATE CueSheet SET Path=?,PathKey=?,DirectoryKey=?,Fingerprint=?,IsActive=1 WHERE Id=?",
                sheet.Path, CuePathKey(sheet.Path), CuePathKey(directory), sheet.Fingerprint, sheetId);
        }

        bool oneTrackPerWhole = sheet.Tracks.Count == sheet.Sources.Count && sheet.Tracks.All(track => track.StartSample == 0);
        if (!oneTrackPerWhole) RestoreWholeVersions(db, sheetId);
        var oldTracks = db.Query<Music>("SELECT * FROM Music WHERE CueSheetId=? ORDER BY CueMappingActive DESC,Id", sheetId);
        var assigned = new Music?[sheet.Tracks.Count];
        var consumed = new HashSet<int>();
        // Exact position plus source title can recover identity even when the displayed track number changes.
        for (int i = 0; i < assigned.Length; i++)
        {
            var next = sheet.Tracks[i];
            var matches = oldTracks.Where(old => !consumed.Contains(old.Id) && SameSource(old, next) &&
                old.StartSample == next.StartSample && old.CueSourceTitle == next.CueSourceTitle).ToArray();
            var active = matches.Where(track => track.CueMappingActive).ToArray();
            if (active.Length == 1) assigned[i] = active[0];
            else if (active.Length == 0 && matches.Length == 1) assigned[i] = matches[0];
            if (assigned[i] is { } matched) consumed.Add(matched.Id);
        }
        for (int i = 0; i < assigned.Length; i++)
        {
            if (assigned[i] is not null) continue;
            var next = sheet.Tracks[i];
            var original = oldTracks.FirstOrDefault(old => old.CueMappingActive && !consumed.Contains(old.Id) &&
                old.CueTrackNo == next.CueTrackNo && SameSource(old, next));
            // A same-number edit can change either the title or start. Preserve versions when both facts change.
            if (original is null || original.StartSample != next.StartSample && original.CueSourceTitle != next.CueSourceTitle) continue;
            assigned[i] = original;
            consumed.Add(original.Id);
        }
        db.Execute("UPDATE Music SET CueMappingActive=0 WHERE CueSheetId=? AND CueMappingActive=1", sheetId);
        int changed = 1;
        foreach (var old in oldTracks)
            if (old.CueMappingActive && !consumed.Contains(old.Id))
                changed += db.Execute("UPDATE Music SET IsMissing=1,MetadataRevision=MetadataRevision+1 WHERE Id=?", old.Id);

        for (int i = 0; i < sheet.Tracks.Count; i++)
        {
            Music track = sheet.Tracks[i];
            Music? old = assigned[i];
            bool reuse = oneTrackPerWhole && old is null;
            if (reuse)
                old = db.FindWithQuery<Music>("SELECT * FROM Music WHERE SourceId=0 AND Path=? COLLATE NOCASE AND COALESCE(CueTrackNo,0)=0 ORDER BY IsDetached,Id LIMIT 1", track.Path);
            track.CueSheetId = sheetId;
            track.CueMappingActive = true;
            if (old is null)
            {
                track.CueReusedWhole = oneTrackPerWhole;
                db.Insert(track);
                MusicMetadataStorage.AttachSong(db, track);
                added.Add(track);
            }
            else
            {
                track.Id = old.Id;
                track.CueReusedWhole = reuse || old.CueReusedWhole;
                CueMetadataOverrides.Apply(db, track);
                db.Execute("""
                    UPDATE Music SET Title=?,TitleFromFileName=?,Author=?,Album=?,Duration=?,FolderPath=?,LastLevelFolderPath=?,Path=?,
                        Extension=?,BitDepth=?,BitRate=?,SampleRate=?,Channel=?,TrackNumber=?,DiskNumber=?,Year=?,CreateTime=?,UpdateTime=?,
                        CodecId=?,EncodingKind=?,CueSheetId=?,CuePath=?,CueTrackNo=?,CueSourceTitle=?,StartSample=?,EndSample=?,CueReusedWhole=?,
                        CueMappingActive=1,IsDetached=0,IsMissing=0,IsCueContainer=0,MetadataRevision=MetadataRevision+1 WHERE Id=?
                    """, track.Title, track.TitleFromFileName, track.Author, track.Album, track.Duration, track.FolderPath, track.LastLevelFolderPath, track.Path,
                    track.Extension, track.BitDepth, track.BitRate, track.SampleRate, track.Channel, track.TrackNumber, track.DiskNumber, track.Year,
                    track.CreateTime, track.UpdateTime, track.CodecId, track.EncodingKind, track.CueSheetId, track.CuePath, track.CueTrackNo,
                    track.CueSourceTitle, track.StartSample, track.EndSample, track.CueReusedWhole, track.Id);
                MusicMetadataStorage.Save(db, track.Id, AdditionalMusicMetadata.From(track));
                if (old.IsMissing || old.IsDetached) added.Add(db.Get<Music>(track.Id));
            }
            changed++;
        }
        db.Execute("DELETE FROM CueSource WHERE CueSheetId=?", sheetId);
        foreach (var source in sheet.Sources)
            db.Execute("INSERT INTO CueSource VALUES(?,?,?,?,?,?)", sheetId, source.Path, source.Revision.Length, source.Revision.LastWriteUtcTicks,
                source.SampleRate, source.TotalSamples);
        return changed;

        bool SameSource(Music old, Music next)
        {
            if (CuePathComparer.Equals(old.Path, next.Path)) return true;
            if (prior is null || priorSources is null) return false;
            bool explicitMove = renames?.TryGetValue(old.Path, out var newPath) == true && CuePathComparer.Equals(newPath, next.Path);
            bool directoryMove = !CuePathComparer.Equals(Path.GetDirectoryName(prior.Path), directory) &&
                CuePathComparer.Equals(Path.GetFileName(old.Path), Path.GetFileName(next.Path));
            if (!explicitMove && !directoryMove) return false;
            var previous = priorSources.FirstOrDefault(source => CuePathComparer.Equals(source.Path, old.Path));
            var incoming = sheet.Sources.First(source => CuePathComparer.Equals(source.Path, next.Path));
            return previous is not null && previous.Length == incoming.Revision.Length && previous.LastWriteUtcTicks == incoming.Revision.LastWriteUtcTicks &&
                previous.SampleRate == incoming.SampleRate && previous.TotalSamples == incoming.TotalSamples;
        }
    }
}
