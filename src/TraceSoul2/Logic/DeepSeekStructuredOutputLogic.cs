using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    public static class DeepSeekStructuredOutputLogic
    {
        public static async Task<T> CompleteAsync<T>(
            ILlmClient client,
            List<DeepSeekMessageData> messages,
            Func<T, bool> validator,
            string missingMessage,
            CancellationToken cancellationToken,
            string promptCacheKey = null,
            Func<T, string> validationError = null,
            Action<T, IReadOnlyCollection<string>> onParsed = null)
            where T : class
        {
            var raw = await client.CompleteJsonAsync(messages, cancellationToken, promptCacheKey);
            Exception firstError;
            T parsed;
            try
            {
                parsed = Parse<T>(raw, onParsed);
                var detail = parsed == null ? null : validationError?.Invoke(parsed);
                if (parsed != null && string.IsNullOrEmpty(detail) && (validator == null || validator(parsed))) return parsed;
                throw new InvalidOperationException(string.IsNullOrEmpty(detail) ? missingMessage : detail);
            }
            catch (Exception exception)
            {
                firstError = exception;
            }

            var repair = new List<DeepSeekMessageData>(messages)
            {
                new DeepSeekMessageData("assistant", Limit(raw, 16000)),
                new DeepSeekMessageData(
                    "user",
                    CorePrompts.Retry.JsonRepairUser(DescribeFailure(firstError, typeof(T))) +
                    " 请重新输出完整 JSON，不要只输出修改片段；字符串中的双引号必须转义，属性和值之间用冒号，属性之间用逗号。")
            };
            var repairedRaw = await client.CompleteJsonAsync(repair, cancellationToken, promptCacheKey);
            try
            {
                parsed = Parse<T>(repairedRaw, onParsed);
                var detail = parsed == null ? null : validationError?.Invoke(parsed);
                if (parsed != null && string.IsNullOrEmpty(detail) && (validator == null || validator(parsed))) return parsed;
                throw new InvalidOperationException(string.IsNullOrEmpty(detail) ? missingMessage : detail);
            }
            catch (Exception secondError)
            {
                throw new InvalidOperationException(
                    "语言模型连续两次返回不可用的结构化输出。首次错误：" + DescribeFailure(firstError, typeof(T)) +
                    "；纠正后错误：" + DescribeFailure(secondError, typeof(T)),
                    secondError);
            }
        }

        // 路径必须能逐段对应实际 DTO，回显代码中的字段名和类型，不回显未知字段或异常正文。
        private static string DescribeFailure(Exception error, Type contract)
        {
            if (error is JsonException json)
            {
                return "JSON 语法或字段类型错误（行 " + ((json.LineNumber ?? 0) + 1) +
                    "，字节位置 " + (json.BytePositionInLine ?? 0) + "）" +
                    ContractTypeHint(contract, json.Path);
            }
            return error is InvalidOperationException
                ? Limit(error.Message, 240) : "结构化输出解析失败";
        }

        private static string ContractTypeHint(Type contract, string path)
        {
            if (string.IsNullOrEmpty(path) || !Regex.IsMatch(path, @"^\$(?:\.[A-Za-z_][A-Za-z_0-9]*(?:\[\d{1,6}\])?)+$")) return "";
            var safe = "$";
            foreach (Match part in Regex.Matches(path, @"\.([A-Za-z_][A-Za-z_0-9]*)(\[\d{1,6}\])?"))
            {
                var field = contract.GetField(part.Groups[1].Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var property = contract.GetProperty(part.Groups[1].Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field == null && property == null) return "";
                safe += "." + (field?.Name ?? property.Name);
                contract = field?.FieldType ?? property.PropertyType;
                if (part.Groups[2].Success)
                {
                    if (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(List<>)) return "";
                    contract = contract.GetGenericArguments()[0]; safe += "[]";
                }
            }
            contract = Nullable.GetUnderlyingType(contract) ?? contract;
            var expected = contract == typeof(bool) ? "布尔 true/false（不加引号）" :
                contract == typeof(string) ? "字符串（文字用双引号包裹）" :
                contract == typeof(int) || contract == typeof(long) ? "整数（不加引号）" :
                contract == typeof(float) || contract == typeof(double) || contract == typeof(decimal) ? "数字（不加引号）" :
                contract == typeof(DateTime) || contract == typeof(DateTimeOffset) ? "日期时间字符串" :
                contract.IsArray || (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(List<>)) ? "数组 [...]" : "对象 {...}";
            return "；字段 " + safe + " 必须是 JSON " + expected + "，请按输出结构修正字段类型。";
        }

        /// <summary>开口：收自然语言。校验失败再请它重说一次，不要求 JSON。</summary>
        public static async Task<string> CompletePlainAsync(
            ILlmClient client,
            List<DeepSeekMessageData> messages,
            Func<string, bool> validator,
            string missingMessage,
            CancellationToken cancellationToken,
            string promptCacheKey = null)
        {
            var raw = await client.CompleteTextAsync(messages, cancellationToken, promptCacheKey);
            if (validator == null || validator(raw)) return raw ?? string.Empty;

            var repair = new List<DeepSeekMessageData>(messages)
            {
                new DeepSeekMessageData("assistant", Limit(raw, 16000)),
                new DeepSeekMessageData(
                    "user",
                    CorePrompts.Retry.SpeakRepairUser(missingMessage))
            };
            var repaired = await client.CompleteTextAsync(repair, cancellationToken, promptCacheKey);
            if (validator == null || validator(repaired)) return repaired ?? string.Empty;
            throw new InvalidOperationException(
                "语言模型连续两次没有把话说出来。首次：" + missingMessage);
        }

        /// <summary>finish_reason 表示输出被长度截断。</summary>
        public static bool LooksLikeTruncatedFinish(string finishReason)
        {
            var reason = (finishReason ?? string.Empty).Trim();
            if (reason.Length == 0) return false;
            return string.Equals(reason, "length", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(reason, "max_tokens", StringComparison.OrdinalIgnoreCase) ||
                   reason.IndexOf("MAX_TOKEN", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>JSON 花括号/字符串未闭合，典型是被截断的结构化输出。</summary>
        public static bool LooksIncompleteJson(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            var first = text.IndexOf('{');
            if (first < 0) return true;
            var depth = 0;
            var inString = false;
            var escape = false;
            for (var i = first; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return inString || depth != 0;
        }

        private static T Parse<T>(string raw, Action<T, IReadOnlyCollection<string>> onParsed = null) where T : class
        {
            var json = EscapeRawControlsInJsonStrings(StripCodeFence(raw));
            var result = TraceJson.FromJson<T>(json);
            if (result != null && onParsed != null)
            {
                using var document = JsonDocument.Parse(json);
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in document.RootElement.EnumerateObject()) fields.Add(field.Name);
                onParsed(result, fields);
            }
            return result;
        }

        /// <summary>
        /// GLM 等模型常在 JSON 字符串里直接断行（未转义 0x0A）。
        /// 解析前把字符串内的裸控制符改成 \\n / \\r / \\t，合法 JSON 不受影响。
        /// </summary>
        public static string EscapeRawControlsInJsonStrings(string json)
        {
            var text = json ?? string.Empty;
            if (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0 && text.IndexOf('\t') < 0)
                return text;
            var builder = new StringBuilder(text.Length + 16);
            var inString = false;
            var escape = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!inString)
                {
                    if (c == '"') inString = true;
                    builder.Append(c);
                    continue;
                }
                if (escape)
                {
                    builder.Append(c);
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    builder.Append(c);
                    escape = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = false;
                    builder.Append(c);
                    continue;
                }
                if (c == '\n') { builder.Append("\\n"); continue; }
                if (c == '\r') { builder.Append("\\r"); continue; }
                if (c == '\t') { builder.Append("\\t"); continue; }
                if (c < ' ')
                {
                    builder.Append("\\u");
                    builder.Append(((int)c).ToString("x4"));
                    continue;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static string StripCodeFence(string value)
        {
            var text = (value ?? string.Empty).Trim();
            var firstBrace = text.IndexOf('{');
            var lastBrace = text.LastIndexOf('}');
            return firstBrace >= 0 && lastBrace > firstBrace
                ? text.Substring(firstBrace, lastBrace - firstBrace + 1)
                : text;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
