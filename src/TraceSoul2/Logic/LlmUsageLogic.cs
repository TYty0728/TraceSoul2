using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 各家 usage 字段名不一样：DeepSeek 用 prompt_cache_hit_tokens，
    /// Kimi 用 cached_tokens，GLM 用 prompt_tokens_details.cached_tokens。
    /// 没上报缓存字段时不要写成 0%，那是「看不见」不是「没命中」。
    /// 解析不同官网渠道返回的缓存 usage 字段；上下文装配策略与这里相互独立。
    /// </summary>
    public static class LlmUsageLogic
    {
        public static LlmUsageData Parse(string responseBody)
        {
            var result = new LlmUsageData();
            if (string.IsNullOrWhiteSpace(responseBody)) return result;
            try
            {
                using (var doc = JsonDocument.Parse(responseBody))
                {
                    var root = doc.RootElement;
                    JsonElement usage;
                    if (TryGetObject(root, "usage", out usage))
                        FillFromUsage(usage, result);
                    JsonElement meta;
                    if (TryGetObject(root, "usageMetadata", out meta))
                        FillFromGemini(meta, result);
                }
            }
            catch (JsonException)
            {
                return result;
            }
            if (result.CacheReported && result.CacheMissTokens <= 0 && result.PromptTokens > 0)
                result.CacheMissTokens = Math.Max(0, result.PromptTokens - result.CacheHitTokens);
            if (result.TotalTokens <= 0 && (result.PromptTokens > 0 || result.CompletionTokens > 0))
                result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }

        public static string FormatDump(LlmUsageData usage)
        {
            if (usage == null || (usage.PromptTokens <= 0 && usage.TotalTokens <= 0 && !usage.CacheReported))
                return string.Empty;
            var builder = new StringBuilder();
            builder.Append("prompt_tokens=").AppendLine(usage.PromptTokens.ToString());
            builder.Append("prompt_cache_hit_tokens=").AppendLine(usage.CacheHitTokens.ToString());
            builder.Append("prompt_cache_miss_tokens=").AppendLine(usage.CacheMissTokens.ToString());
            builder.Append("prompt_cache_hit_rate=").AppendLine(FormatRate(usage));
            builder.Append("cache_reported=").AppendLine(usage.CacheReported ? "true" : "false");
            if (!string.IsNullOrWhiteSpace(usage.CacheField))
                builder.Append("cache_field=").AppendLine(usage.CacheField);
            builder.Append("completion_tokens=").AppendLine(usage.CompletionTokens.ToString());
            if (usage.ReasoningTokens > 0)
                builder.Append("reasoning_tokens=").AppendLine(usage.ReasoningTokens.ToString());
            builder.Append("total_tokens=").AppendLine(usage.TotalTokens.ToString());
            return builder.ToString();
        }

        public static string FormatLog(LlmUsageData usage)
        {
            if (usage == null || (usage.PromptTokens <= 0 && usage.CompletionTokens <= 0 && !usage.CacheReported))
                return string.Empty;
            var builder = new StringBuilder();
            builder.Append("输入 ").Append(usage.PromptTokens);
            if (!usage.CacheReported)
                builder.Append(" 缓存未上报");
            else
            {
                builder.Append(" 命中 ").Append(usage.CacheHitTokens)
                    .Append("（").Append(FormatRate(usage)).Append("）")
                    .Append(" 未命中 ").Append(usage.CacheMissTokens);
            }
            builder.Append("｜输出 ").Append(usage.CompletionTokens);
            if (usage.ReasoningTokens > 0)
                builder.Append("（思考 ").Append(usage.ReasoningTokens).Append("）");
            return builder.ToString();
        }

        public static string FormatRate(LlmUsageData usage)
        {
            if (usage == null || !usage.CacheReported) return "未上报";
            var denom = usage.PromptTokens > 0
                ? usage.PromptTokens
                : usage.CacheHitTokens + usage.CacheMissTokens;
            if (denom <= 0) return "0.0%";
            var rate = Math.Min(100d, usage.CacheHitTokens * 100d / denom);
            return rate.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static void FillFromUsage(JsonElement usage, LlmUsageData result)
        {
            int value;
            if (TryGetInt(usage, "prompt_tokens", out value)) result.PromptTokens = value;
            if (TryGetInt(usage, "completion_tokens", out value)) result.CompletionTokens = value;
            if (TryGetInt(usage, "total_tokens", out value)) result.TotalTokens = value;
            if (TryGetInt(usage, "prompt_cache_hit_tokens", out value))
                SetHit(result, value, "prompt_cache_hit_tokens");
            else if (TryGetInt(usage, "cached_tokens", out value))
                SetHit(result, value, "cached_tokens");
            else if (TryGetInt(usage, "cache_read_input_tokens", out value))
                SetHit(result, value, "cache_read_input_tokens");
            else
            {
                JsonElement details;
                if (TryGetObject(usage, "prompt_tokens_details", out details) &&
                    TryGetInt(details, "cached_tokens", out value))
                    SetHit(result, value, "prompt_tokens_details.cached_tokens");
            }
            if (TryGetInt(usage, "prompt_cache_miss_tokens", out value))
                result.CacheMissTokens = value;
            JsonElement completionDetails;
            if (TryGetObject(usage, "completion_tokens_details", out completionDetails) &&
                TryGetInt(completionDetails, "reasoning_tokens", out value))
                result.ReasoningTokens = value;
            else if (TryGetInt(usage, "reasoning_tokens", out value))
                result.ReasoningTokens = value;
        }

        private static void FillFromGemini(JsonElement meta, LlmUsageData result)
        {
            int value;
            if (result.PromptTokens <= 0 && TryGetInt(meta, "promptTokenCount", out value))
                result.PromptTokens = value;
            if (result.CompletionTokens <= 0 && TryGetInt(meta, "candidatesTokenCount", out value))
                result.CompletionTokens = value;
            if (result.TotalTokens <= 0 && TryGetInt(meta, "totalTokenCount", out value))
                result.TotalTokens = value;
            if (!result.CacheReported && TryGetInt(meta, "cachedContentTokenCount", out value))
                SetHit(result, value, "usageMetadata.cachedContentTokenCount");
            if (result.ReasoningTokens <= 0 && TryGetInt(meta, "thoughtsTokenCount", out value))
                result.ReasoningTokens = value;
        }

        private static void SetHit(LlmUsageData result, int hit, string field)
        {
            result.CacheReported = true;
            result.CacheHitTokens = Math.Max(0, hit);
            result.CacheField = field;
        }

        private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out value) &&
                value.ValueKind == JsonValueKind.Object)
                return true;
            value = default(JsonElement);
            return false;
        }

        private static bool TryGetInt(JsonElement parent, string name, out int value)
        {
            JsonElement prop;
            value = 0;
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out prop))
                return false;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
                return true;
            long wide;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out wide))
            {
                value = (int)Math.Max(0, Math.Min(int.MaxValue, wide));
                return true;
            }
            return false;
        }
    }

    public sealed class LlmUsageData
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public int CacheHitTokens { get; set; }
        public int CacheMissTokens { get; set; }
        public int ReasoningTokens { get; set; }
        public bool CacheReported { get; set; }
        public string CacheField { get; set; }
    }
}
