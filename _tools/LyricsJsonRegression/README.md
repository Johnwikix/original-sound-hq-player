# Lyrics JSON migration regression

Run from the repository root:

```powershell
dotnet run --project _tools/LyricsJsonRegression -c Release
dotnet publish _tools/LyricsJsonRegression -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:TrimMode=full -o _tools/LyricsJsonRegression/bin/trimmed
& _tools/LyricsJsonRegression/bin/trimmed/LyricsJsonRegression.exe
dotnet run --project _tools/LyricsCoverRegression -c Release
dotnet build WinUIMusicPlayer.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false -p:AppxSymbolPackageEnabled=false
```

The regression executable disables reflection serialization. Neither it nor the helper uses `TrimmerRootAssembly`. The original Newtonsoft dependency exists only in the optional baseline generator's reference to the old source tree; it is not a dependency of the regression executable or production helper.

`Fixtures/models.jsonl` contains three lines per model: input, the original serialized result, and the original empty-object result. `ModelChecks.cs` supplies strongly typed calls for all 191 provider/parser DTOs, including nested types. Checks compare the JSON character for character, preserving ordering, null fields, computed properties, floating-point markers and escaping. Other checks cover collections, scalar coercion, comments, casing, trailing commas, null/empty inputs and compact/indented nested request dictionaries.

`ParserScenarios.cs` is compiled against both libraries. Its golden snapshots cover Spotify sync modes and syllables, Musixmatch richsync/subtitle/plain fallback and language precedence, YRC credits, KRC translations, timestamps and generated LRC/YRC/KRC output. `LyricsCoverRegression` additionally exercises the real lyrics service with intercepted HTTP responses, including business failures, cancellation and provider fallback. No real provider/account traffic or WinUI interaction is used here.

To reproduce the original fixtures, run `./_tools/LyricsJsonRegression/RegenerateBaseline.ps1`. It exports commit `caa356c493d1823c7d165b2d4adff139c69b8f7d` into the ignored `obj/source` directory. The generator rewrites fixtures, typed model checks and the explicit model registry in `LyricsJsonContext.cs`; review those changes when adding models. Normal tests never regenerate expected output from the migrated implementation.

Compatibility boundaries for callers of this library:

- Existing provider/parser signatures and wire-property names remain unchanged. The optional `Newtonsoft.Json.Formatting` parameter of `JsonUtils.ToJson` becomes `bool writeIndented`; repository callers need no changes.
- Arbitrary external DTOs must use the new `JsonTypeInfo<T>` overloads; source-generated metadata replaces reflection fallback. The parameterless helpers cover registered library models and request types.
- Untyped primitive values retain CLR string/bool/Int64/double/DateTime values; untyped objects/arrays use independently owned `JsonElement` instead of `JObject`/`JArray`. External callers inspecting those objects must use the System.Text.Json DOM.
- These fixtures establish equivalence for the covered provider contracts and parser outputs, not every behavior of Json.NET as a general-purpose JSON library. Nonstandard single-quoted/unquoted JSON is not supported by System.Text.Json; malformed JSON throws its `JsonException` rather than Newtonsoft exception types.

Validation on 2026-09-20: normal and full-trim regression each passed 1,010 checks with reflection disabled; the existing lyrics/cover service regression passed 15 cases. The suite also verifies custom DTO metadata overloads and the absence of the Newtonsoft runtime dependency. Main Release compilation succeeded with packaging disabled; default MSIX packaging could not find the required tool. The application still has unrelated existing analyzer/trim warnings.

The small timing/allocation probe is deliberately labeled informal: Windows x64, .NET 10, Release, 1,000 warmup and 10,000 measured parses of one Spotify line. Original: approximately 49–52 ms and 3,432 bytes/op; migrated normal build: approximately 39–42 ms and 1,168 bytes/op. Full-trim executions while other builds were active varied from 48–77 ms, so these timings do not establish a reliable throughput improvement. It excludes cold initialization, networking, large songs and UI work; it is not a BenchmarkDotNet or EventPipe study and cannot establish application-wide gains.
