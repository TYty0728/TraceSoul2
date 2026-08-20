using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
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
                    "上一条不满足要求：" + (missingMessage ?? "不是完整合法的 JSON，或缺少必填字段") +
                    "。这不是新输入。请保持原任务语义，重新输出一个完整 JSON 对象；" +
                    "不要解释，不要 Markdown，并闭合全部字符串与数组。")
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
                return TraceJson.FromJson<T>(StripCodeFence(raw));
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
