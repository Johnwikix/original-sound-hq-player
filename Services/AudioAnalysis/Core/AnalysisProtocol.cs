using System.Text.Json.Serialization;
namespace WinUIMusicPlayer.AudioAnalysis;
/// <summary>固定三片段任务；只允许本地绝对路径。</summary>
public sealed record AnalysisRequest(string Path);
/// <summary>返回三个原始128维片段，校准由主程序按曲库完成。</summary>
public sealed record AnalysisReply(float[]? Features, string Error = "");
[JsonSerializable(typeof(AnalysisRequest))]
[JsonSerializable(typeof(AnalysisReply))]
public partial class AnalysisJsonContext : JsonSerializerContext;
