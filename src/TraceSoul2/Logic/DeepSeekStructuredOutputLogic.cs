using System;
using System.Collections.Generic;
using System.Text;
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
            CancellationToken cancellationToken)
            where T : class
        {
            var raw = await client.CompleteJsonAsync(messages, cancellationToken);
            Exception firstError;
            T parsed;
            try
            {
                parsed = Parse<T>(raw);
                if (parsed != null && (validator == null || validator(parsed))) return parsed;
                throw new InvalidOperationException(missingMessage);
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
                    CorePrompts.Retry.JsonRepairUser(missingMessage))
            };
            var repairedRaw = await client.CompleteJsonAsync(repair, cancellationToken);
            try
            {
                parsed = Parse<T>(repairedRaw);
                if (parsed != null && (validator == null || validator(parsed))) return parsed;
                throw new InvalidOperationException(missingMessage);
            }
            catch (Exception secondError)
            {
                throw new InvalidOperationException(
                    "语言模型连续两次返回不可用的结构化输出。首次错误：" + firstError.Message,
                    secondError);
            }
        }

        /// <summary>开口：收自然语言。校验失败再请它重说一次，不要求 JSON。</summary>
        public static async Task<string> CompletePlainAsync(
            ILlmClient client,
            List<DeepSeekMessageData> messages,
            Func<string, bool> validator,
            string missingMessage,
            CancellationToken cancellationToken)
        {
            var raw = await client.CompleteTextAsync(messages, cancellationToken);
            if (validator == null || validator(raw)) return raw ?? string.Empty;

            var repair = new List<DeepSeekMessageData>(messages)
            {
                new DeepSeekMessageData("assistant", Limit(raw, 16000)),
                new DeepSeekMessageData(
                    "user",
                    CorePrompts.Retry.SpeakRepairUser(missingMessage))
            };
            var repaired = await client.CompleteTextAsync(repair, cancellationToken);
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

        private static T Parse<T>(string raw) where T : class
        {
            return TraceJson.FromJson<T>(EscapeRawControlsInJsonStrings(StripCodeFence(raw)));
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
