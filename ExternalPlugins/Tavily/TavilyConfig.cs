using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TraceSoul2.ExternalPlugins.Tavily;

public sealed class TavilyConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("api_key")] public string ApiKey { get; init; } = "";
    [JsonPropertyName("search_depth")] public string SearchDepth { get; init; } = "basic";
    [JsonPropertyName("max_results")] public int MaxResults { get; init; } = 5;
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; init; } = 25;

    public bool Available => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public static TavilyConfig Load(string? packageDirectory, string? dataDirectory)
    {
        try
        {
            var merged = new JsonObject();
            foreach (var path in new[] {
                string.IsNullOrEmpty(packageDirectory) ? null : Path.Combine(packageDirectory, "plugin.json"),
                string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(dataDirectory, "config.json") })
            {
                if (path == null || !File.Exists(path)) continue;
                var source = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new JsonException();
                foreach (var entry in source) merged[entry.Key] = entry.Value?.DeepClone();
            }
            var config = merged.Deserialize<TavilyConfig>() ?? throw new JsonException();
            config.Validate();
            return config;
        }
        catch { throw new InvalidOperationException("Tavily 配置无效，请检查字段类型、搜索模式和数值范围。"); }
    }

    public void Validate()
    {
        if (ApiKey == null || ApiKey.Length > 512 || ApiKey.Any(char.IsControl) ||
            SearchDepth is not ("basic" or "fast" or "ultra-fast" or "advanced") ||
            MaxResults is < 1 or > 5 || TimeoutSeconds is < 5 or > 60)
            throw new InvalidOperationException("Tavily 配置无效。");
    }
}
