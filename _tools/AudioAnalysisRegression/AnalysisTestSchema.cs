using SQLite;

internal static class AnalysisTestSchema
{
    internal static async Task CreateMusicAsync(SQLiteAsyncConnection database)
    {
        await database.ExecuteAsync("""
            CREATE TABLE Music(Id INTEGER PRIMARY KEY, SourceId INTEGER, Path TEXT, Title TEXT,
                IsMissing INTEGER DEFAULT 0, IsDetached INTEGER DEFAULT 0, IsCueContainer INTEGER DEFAULT 0, CueSheetId INTEGER,
                CueTrackNo INTEGER DEFAULT 0, CueMappingActive INTEGER DEFAULT 0, StartSample INTEGER DEFAULT 0,
                EndSample INTEGER DEFAULT 0, SampleRate INTEGER DEFAULT 0)
            """);
        await database.ExecuteAsync("""
            CREATE TABLE CueSource(CueSheetId INTEGER NOT NULL, Path TEXT NOT NULL COLLATE NOCASE,
                Length INTEGER NOT NULL, LastWriteUtcTicks INTEGER NOT NULL, SampleRate INTEGER NOT NULL,
                TotalSamples INTEGER NOT NULL, PRIMARY KEY(CueSheetId,Path)) WITHOUT ROWID
            """);
    }
}
