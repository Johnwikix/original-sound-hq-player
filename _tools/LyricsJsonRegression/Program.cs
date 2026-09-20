using Lyricify.Lyrics.Serialization;
using System.Diagnostics;

partial class Program
{
    static int checks;
    static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    static readonly string[] ModelFixtures = File.ReadAllLines(Path.Combine(FixtureDirectory, "models.jsonl"));

    static void Main()
    {
        try { Run(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 1;
        }
    }

    static void Run()
    {
        CheckModels();
        CheckCustomContracts();
        CheckParsers();
        Equal(File.ReadAllText(Path.Combine(FixtureDirectory, "request.json")), LyricsJson.Serialize(RequestScenarios.Create()), "nested request payload");
        Equal(File.ReadAllText(Path.Combine(FixtureDirectory, "request-indented.json")), LyricsJson.Serialize(RequestScenarios.Create(), true), "indented request payload");
        var parserResults = ParserScenarios.Run().ToArray();
        var parserBaseline = File.ReadAllLines(Path.Combine(FixtureDirectory, "parsers.txt"));
        Equal(parserBaseline.Length.ToString(), parserResults.Length.ToString(), "parser case count");
        for (int i = 0; i < parserResults.Length; i++) Equal(parserBaseline[i], parserResults[i], $"parser/generator baseline {i}");
        Equal("null", LyricsJson.Serialize<string?>(null), "null serialization");
        Equal("\"NaN\"", LyricsJson.Serialize(double.NaN), "NaN");
        Equal("1.0", LyricsJson.Serialize(1d), "floating point marker");
        Equal("System.String", LyricsJson.Deserialize<object>("\"alias\"")!.GetType().FullName, "untyped string retains CLR type");
        Equal("System.Int64", LyricsJson.Deserialize<object>("42")!.GetType().FullName, "untyped integer retains CLR type");
        Equal("System.Boolean", LyricsJson.Deserialize<object>("true")!.GetType().FullName, "untyped boolean retains CLR type");
        Equal("True", LyricsJson.Deserialize<bool>("1").ToString(), "numeric boolean");
        Equal("True", LyricsJson.Deserialize<bool>("\"true\"").ToString(), "string boolean");
        Equal("123", LyricsJson.Deserialize<int>("\"123\"").ToString(), "quoted integer");
        Equal("歌词", LyricsJson.Deserialize<Lyricify.Lyrics.Parsers.Models.Spotify.SpotifyLyricsLine>("/*comment*/{\"WORDS\":\"歌词\",\"unknown\":42,}")!.Words, "case insensitive names, comments, trailing comma");
        Console.WriteLine($"PASS: {checks} compatibility checks; reflection serialization enabled: {System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault}");
        Measure();
    }

    static void CheckModel<T>(int id)
    {
        string input = ModelFixtures[id * 3];
        var actual = LyricsJson.Deserialize<T>(input);
        Equal(ModelFixtures[id * 3 + 1], LyricsJson.Serialize(actual), typeof(T).FullName!);
        Equal(ModelFixtures[id * 3 + 2], LyricsJson.Serialize(LyricsJson.Deserialize<T>("{}")), typeof(T).FullName! + " empty");
        Equal("null", LyricsJson.Serialize(LyricsJson.Deserialize<T>("null")), typeof(T).FullName! + " null");
        Equal("null", LyricsJson.Serialize(LyricsJson.Deserialize<T>(" \r\n\t")), typeof(T).FullName! + " whitespace");
        Equal("[" + LyricsJson.Serialize(actual) + "]", LyricsJson.Serialize(LyricsJson.Deserialize<List<T>>("[" + input + "]")), typeof(T).FullName! + " list");
    }

    static void Equal(string? expected, string? actual, string label)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{label}\nExpected: {expected}\nActual:   {actual}");
        checks++;
    }

    static void Measure()
    {
        const string input = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[{\"startTimeMs\":\"1000\",\"endTimeMs\":\"2000\",\"words\":\"歌词 hello\"}]}}";
        for (int i = 0; i < 1000; i++) Lyricify.Lyrics.Parsers.SpotifyParser.Parse(input);
        long start = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) Lyricify.Lyrics.Parsers.SpotifyParser.Parse(input);
        watch.Stop();
        Console.WriteLine($"Spotify parse, 10000 iterations: {watch.Elapsed.TotalMilliseconds:F2} ms, {(GC.GetAllocatedBytesForCurrentThread() - start) / 10000} bytes/op (warm process, informal measurement)");
    }
}
