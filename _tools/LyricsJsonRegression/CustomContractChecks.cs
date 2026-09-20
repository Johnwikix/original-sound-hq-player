using Lyricify.Lyrics.Providers.Web;
using System.Text.Json.Serialization;

partial class Program
{
    static void CheckCustomContracts()
    {
        var value = "{\"Text\":\"custom\"}".ToEntity(CustomContractContext.Default.CustomContract)!;
        Equal("custom", value.Text, "external DTO generated metadata");
        Equal("{\"Text\":\"custom\"}", value.ToJson(CustomContractContext.Default.CustomContract), "external DTO serialization");
        Equal("custom", "[{\"Text\":\"custom\"}]".ToEntityList(CustomContractContext.Default.ListCustomContract)![0].Text, "external DTO list");
        Equal("False", File.Exists(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll")).ToString(), "no Newtonsoft runtime dependency");
    }
}

internal sealed class CustomContract
{
    public string? Text { get; set; }
}

[JsonSerializable(typeof(CustomContract))]
[JsonSerializable(typeof(List<CustomContract>))]
internal partial class CustomContractContext : JsonSerializerContext;
