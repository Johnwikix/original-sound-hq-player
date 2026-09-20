using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Reflection;
using System.Text;
using Lyricify.Lyrics.Parsers;

var output = Path.GetFullPath("_tools/LyricsJsonRegression/Fixtures");
Directory.CreateDirectory(output);
var assembly = typeof(SpotifyParser).Assembly;
var resolver = new DefaultContractResolver();
var types = assembly.GetExportedTypes().Where(t => t.IsClass && !t.IsAbstract && !typeof(Exception).IsAssignableFrom(t) &&
    (t.Namespace?.Contains(".Providers.Web.") == true && t.Name != "Api" ||
     t.Namespace?.StartsWith("Lyricify.Lyrics.Parsers.Models") == true ||
     t.FullName?.StartsWith("Lyricify.Lyrics.Decrypter.Krc.Kugou") == true))
    .Where(t => t.GetConstructor(Type.EmptyTypes) != null).OrderBy(t => t.FullName).ToArray();
var checks = new StringBuilder("using Lyricify.Lyrics.Serialization;\n\npartial class Program\n{\n    static void CheckModels()\n    {\n");
var attrs = new StringBuilder("using System.Text.Json.Serialization;\n\nnamespace Lyricify.Lyrics.Serialization;\n\n// Explicit contracts for provider DTOs and their collections; no reflection fallback.\n");
var modelFixtures = new List<string>();
int id = 0;
foreach (var type in types)
{
    string name = type.FullName!.Replace('+', '.');
    attrs.AppendLine($"[JsonSerializable(typeof(global::{name}), TypeInfoPropertyName = \"{name.Replace('.', '_')}\")]");
    attrs.AppendLine($"[JsonSerializable(typeof(List<global::{name}>), TypeInfoPropertyName = \"List_{name.Replace('.', '_')}\")]");
    attrs.AppendLine($"[JsonSerializable(typeof(global::{name}[]), TypeInfoPropertyName = \"Array_{name.Replace('.', '_')}\")]");
    var input = Sample(type, 0).ToString(Formatting.None);
    modelFixtures.Add(input);
    var value = JsonConvert.DeserializeObject(input, type);
    modelFixtures.Add(JsonConvert.SerializeObject(value));
    modelFixtures.Add(JsonConvert.SerializeObject(JsonConvert.DeserializeObject("{}", type)));
    checks.AppendLine($"        CheckModel<global::{name}>({id});");
    id++;
}
checks.AppendLine("    }\n}");
File.WriteAllText("_tools/LyricsJsonRegression/ModelChecks.cs", checks.ToString());
string[] extras = ["Dictionary<string, string>", "Dictionary<string, object>", "string", "int", "long", "bool", "double", "float", "string[]", "int[]", "long[]", "List<object>", "object", "System.Text.Json.JsonElement", "DateTime"];
foreach (var extra in extras) attrs.AppendLine($"[JsonSerializable(typeof({extra}))]");
attrs.AppendLine("public partial class LyricsJsonContext : JsonSerializerContext;");
File.WriteAllText("External/Lyricify.Lyrics.Helper/Serialization/LyricsJsonContext.cs", attrs.ToString());
File.WriteAllLines(Path.Combine(output, "models.jsonl"), modelFixtures);
File.WriteAllText(Path.Combine(output, "request.json"), JsonConvert.SerializeObject(RequestScenarios.Create()));
File.WriteAllText(Path.Combine(output, "request-indented.json"), JsonConvert.SerializeObject(RequestScenarios.Create(), Formatting.Indented));
Console.WriteLine($"Generated {id} model baselines.");
File.WriteAllLines(Path.Combine(output, "parsers.txt"), ParserScenarios.Run());
const string measuredInput = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[{\"startTimeMs\":\"1000\",\"endTimeMs\":\"2000\",\"words\":\"歌词 hello\"}]}}";
for (int i = 0; i < 1000; i++) SpotifyParser.Parse(measuredInput);
long start = GC.GetAllocatedBytesForCurrentThread();
var watch = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 10000; i++) SpotifyParser.Parse(measuredInput);
watch.Stop();
Console.WriteLine($"Original Spotify parse, 10000 iterations: {watch.Elapsed.TotalMilliseconds:F2} ms, {(GC.GetAllocatedBytesForCurrentThread() - start) / 10000} bytes/op");

JToken Sample(Type type, int depth)
{
    type = Nullable.GetUnderlyingType(type) ?? type;
    if (type == typeof(string)) return new JValue("歌词 <>&'\" \\ /\b\f\n\r\t\u0001\u001f\u0085\u2028\u2029 😀");
    if (type == typeof(bool)) return new JValue(true);
    if (type == typeof(DateTime)) return new JValue(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    if (type.IsEnum) return new JValue(0);
    if (type.IsPrimitive || type == typeof(decimal)) return new JValue(12);
    if (depth > 5) return JValue.CreateNull();
    if (type == typeof(object)) return JToken.Parse("{\"n\":1,\"s\":\"歌词\",\"a\":[true,null,2.5]}");
    if (type.IsArray) return new JArray(Sample(type.GetElementType()!, depth + 1));
    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return new JArray(Sample(type.GetGenericArguments()[0], depth + 1));
    var result = new JObject();
    if (resolver.ResolveContract(type) is JsonObjectContract contract)
        foreach (var property in contract.Properties.Where(p => !p.Ignored && p.Writable))
            result[property.PropertyName!] = Sample(property.PropertyType!, depth + 1);
    return result;
}
