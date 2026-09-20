internal static class RequestScenarios
{
    internal static Dictionary<string, object> Create() => new()
    {
        ["req_0"] = new Dictionary<string, object>
        {
            ["method"] = "CgiGetVkey",
            ["module"] = "vkey.GetVkeyServer",
            ["param"] = new Dictionary<string, object>
            {
                ["songmid"] = new[] { "歌曲 <>&'\"\\ /\u0001\u0085\u2028\u2029 😀" },
                ["songtype"] = new[] { 1 },
                ["loginflag"] = 1,
                ["platform"] = "20",
                ["enabled"] = true,
                ["missing"] = null!,
                ["ratio"] = 1.0,
            },
        },
        ["header"] = new Dictionary<string, string> { ["os"] = "pc", ["appver"] = "2.0.3" },
    };
}
