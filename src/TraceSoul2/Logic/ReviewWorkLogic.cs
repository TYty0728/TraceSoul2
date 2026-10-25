using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>Durable, input-bound prose jobs. Structural errors never become deferred prose.</summary>
    public static class ReviewWorkLogic
    {
        public const string StoreId = "review-work-v1";
        public sealed class TextPart
        {
            public string Path;
            public int Limit;
            public Func<string> Read;
            public Action<string> Write;
            public TextPart(string path, int limit, Func<string> read, Action<string> write)
            { Path = path; Limit = limit; Read = read; Write = write; }
        }
        public sealed class Job<T>
        {
            public T Draft;
            public bool Complete;
            public int Attempts;
            public long NextAttemptUnixMs;
            public List<string> Pending = new();
        }
        public sealed class TextOutput { public string text; }
        private static string FieldContext<T>(T draft, string path)
        {
            JsonNode node = JsonNode.Parse(TraceJson.ToJson(draft));
            var parentPath = path.Contains('.') ? path.Substring(0, path.LastIndexOf('.')) : "";
            foreach (Match part in Regex.Matches(parentPath, @"([A-Za-z_][A-Za-z_0-9]*)(?:\[(\d+)\])?"))
            {
                node = node?[part.Groups[1].Value];
                if (part.Groups[2].Success) node = node?[int.Parse(part.Groups[2].Value)];
            }
            if (node is JsonObject obj)
            {
                var field = path.Substring(path.LastIndexOf('.') + 1);
                obj.Remove(field);
                foreach (var pair in obj.ToList())
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 120)
                        obj[pair.Key] = "[长正文省略，按完整来源提炼]";
            }
            var result = node?.ToJsonString() ?? "{}";
            if (result.Length > 12000) throw new ReviewProsePendingException("字段定位上下文超过预算，草稿保留待拆分。");
            return result;
        }
        public static async Task<T> CompleteAsync<T>(SqliteMemoryManager store, string scope, ILlmClient llm,
            List<DeepSeekMessageData> messages, Func<T, string> structure, Func<T, IEnumerable<TextPart>> fields,
            CancellationToken token = default, T seed = null) where T : class
        {
            var key = scope + ":" + IdentityReorganizationLogic.Digest(messages);
            Job<T> job;
            lock (store) job = PuzzleViewLogic.Read<Job<T>>(store.LoadPluginDocument(StoreId, key)) ?? new();
            void Save() { lock (store) store.SavePluginDocument(StoreId, key, TraceJson.ToJson(job)); }
            string Error(T value) => value == null ? "输出必须为对象。" : structure?.Invoke(value);
            if (job.Draft != null && Error(job.Draft) != null)
                throw new InvalidOperationException("复盘草稿结构或来源不再有效：" + Error(job.Draft));
            if (job.Complete) return job.Draft;
            if (job.NextAttemptUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new ReviewProsePendingException("本项正文待整理，已保存草稿并按预算退避。");
            if (job.Draft == null)
            {
                job.Draft = seed ?? await DeepSeekStructuredOutputLogic.CompleteAsync<T>(llm, messages, null,
                    "复盘输出缺少必要结构。", token, validationError: Error, includeInvalidResponse: false);
                var error = Error(job.Draft);
                if (error != null) throw new InvalidOperationException(error);
                Save();
            }
            var targets = (fields?.Invoke(job.Draft) ?? Array.Empty<TextPart>())
                .Where(x => (x.Read()?.Trim().Length ?? 0) > x.Limit).ToList();
            job.Pending = targets.Select(x => x.Path).ToList();
            if (targets.Count > 0)
            {
                job.Attempts++;
                // Persist before calling the model: a crash cannot reset the backoff or attempt count.
                job.NextAttemptUnixMs = DateTimeOffset.UtcNow.AddMinutes(Math.Min(1440, 15 * Math.Pow(4, Math.Min(job.Attempts - 1, 4)))).ToUnixTimeMilliseconds();
                Save();
            }
            foreach (var field in targets.Take(8))
            {
                token.ThrowIfCancellationRequested();
                // Original evidence, not the runaway draft. Only this field may change.
                var repair = new List<DeepSeekMessageData>(messages) {
                    new("user", "待整理对象（正文仅作定位，原始依据以上文为准）：" + FieldContext(job.Draft, field.Path) + "\n【单字段篇幅整理】只根据以上原始依据重新提炼字段 " + field.Path +
                        "，目标" + Math.Max(1, field.Limit / 2) + "字，上限" + field.Limit +
                        "字。保留关键事实、适用条件与反证，不逐条复述。其他字段和来源由程序保留。" +
                        "本次只输出JSON {\"text\":\"该字段完整正文\"}，不要输出原任务的其他字段。") };
                var result = await DeepSeekStructuredOutputLogic.CompleteAsync<TextOutput>(llm, repair,
                    x => !string.IsNullOrWhiteSpace(x?.text), "text必须为非空完整正文。", token, includeInvalidResponse: false);
                token.ThrowIfCancellationRequested();
                if (result.text.Trim().Length <= field.Limit)
                {
                    field.Write(result.text.Trim());
                    var error = Error(job.Draft);
                    if (error != null) throw new InvalidOperationException(error);
                    job.Pending.Remove(field.Path);
                    Save();
                }
            }
            if (job.Pending.Count > 0)
                throw new ReviewProsePendingException("正文待整理：" + string.Join("、", job.Pending) + "；合格字段及完整来源已保存。");
            job.Complete = true; job.NextAttemptUnixMs = 0; Save();
            return job.Draft;
        }
    }
}
