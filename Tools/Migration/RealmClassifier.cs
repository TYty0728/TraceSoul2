using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Migrate
{
    /// <summary>为 unclassified 的导入 Moment 批量做 Realm 分类（规则直判 + LLM 批量 pass）。</summary>
    public static class RealmClassifier
    {
        [Serializable]
        public sealed class RealmBatchItemData
        {
            public string event_id;
            public string realm;
        }

        [Serializable]
        public sealed class RealmBatchOutputData
        {
            public List<RealmBatchItemData> items = new List<RealmBatchItemData>();
        }

        public static async Task<int> RunAsync(MigrationContext context, string[] args)
        {
            var range = DateRange.Parse(args);
            var batchSize = Math.Max(10, Math.Min(120, int.TryParse(CliArgs.Value(args, "--batch", "100"), out var parsed) ? parsed : 100));
            var parallelism = int.TryParse(CliArgs.Value(args, "--parallel", "4"), out var parallel)
                ? Math.Max(1, Math.Min(8, parallel)) : 4;
            var pair = context.RequirePair();
            context.RequireLlm();

            var pending = new List<MomentRecord>();
            foreach (var day in EnumerateDays(context, range))
            {
                var startMs = range.DayStartMs(day);
                var endMs = range.DayEndMs(day);
                pending.AddRange(context.Migration.GetUnclassifiedMomentsInRange(startMs, endMs, int.MaxValue));
            }
            var batches = new List<List<MomentRecord>>();
            for (var i = 0; i < pending.Count; i += batchSize)
                batches.Add(pending.GetRange(i, Math.Min(batchSize, pending.Count - i)));

            Console.WriteLine("分类：未分类 " + pending.Count + " 条，共 " + batches.Count
                + " 批（每批 " + batchSize + "，并发 " + parallelism + "）。");
            var totalUpdated = 0;
            var failures = 0;
            var gate = new SemaphoreSlim(parallelism);
            var updateLock = new object();
            var progress = 0;
            var tasks = batches.Select(batch => Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    var client = context.CreateLlmClient();
                    if (client == null) throw new InvalidOperationException("没有可用的语言模型提供商。");
                    var updated = await ClassifyBatchAsync(context, client, pair, batch);
                    lock (updateLock)
                    {
                        totalUpdated += updated;
                        progress += batch.Count;
                        Console.WriteLine("分类进度：" + progress + "/" + pending.Count
                            + "（本批更新 " + updated + "）");
                    }
                }
                catch (Exception exception)
                {
                    lock (updateLock)
                    {
                        failures += 1;
                        progress += batch.Count;
                        Console.WriteLine("分类批次失败（跳过，重跑可续）：" + exception.Message);
                    }
                }
                finally
                {
                    gate.Release();
                }
            })).ToArray();
            await Task.WhenAll(tasks);

            // 确定性兜底：括号/方括号占位符（表情、图片等平台产物）→ meta。
            // 其余极短消息保持 unclassified（孤立短消息无 Realm 信号，属 §4.5 的诚实分类）。
            var placeholders = context.Migration.ApplyPlaceholderRealmFallback();
            if (placeholders > 0)
                Console.WriteLine("占位符兜底：将 " + placeholders + " 条平台占位符归为 meta。");

            Console.WriteLine("Realm 分类完成：更新 " + totalUpdated + " 条，共 " + batches.Count
                + " 批" + (failures > 0 ? "，失败 " + failures + " 批（重跑可续）" : string.Empty) + "。");
            return 0;
        }

        private static async Task<int> ClassifyBatchAsync(
            MigrationContext context, ILlmClient llm, PairIdentity pair, List<MomentRecord> batch)
        {
            var system = new StringBuilder();
            system.AppendLine(pair.Apply("你是 TraceSoul2 记忆导入器的现实层分类算法，不是 {assname} 本人。"));
            system.AppendLine("把每条文字 Moment 分到四层现实之一：");
            system.AppendLine("- external_world：她在外部真实世界的生活自述与客观外部事实（上班、吃饭、天气、身体、新闻）。");
            system.AppendLine("- shared_scene：两人在共享文字场景中的互动（摸头、拥抱、亲吻、光点、一起听歌、一起做的事）。");
            system.AppendLine("- meta：关于 AI、系统、记忆、角色设定本身的讨论。");
            system.AppendLine("- explicit_fiction：明确的创作、小说、虚构故事。");
            system.AppendLine("- unclassified：实在无法判断时保留。");
            system.AppendLine();
            system.AppendLine("硬规则：");
            system.AppendLine(pair.Apply("1. 文字互动一律算 shared_scene；只判断层次，不判断真假。'{username} 我上班啦'是 external_world 的自述。"));
            system.AppendLine("2. 讨论记忆怎么存、插件怎么工作、提示词是什么，属于 meta。");
            system.AppendLine("3. 只输出 JSON：{\"items\":[{\"event_id\":\"#后面的编号\",\"realm\":\"external_world\"}]}，覆盖下面每一条，event_id 只填编号数字。");
            system.AppendLine();
            system.AppendLine("待分类 Moment：");
            foreach (var moment in batch)
            {
                var preview = (moment.Content ?? string.Empty).Replace('\n', ' ');
                if (preview.Length > 160) preview = preview.Substring(0, 160);
                system.AppendLine("- #" + StartLine(moment.SourceEventId) + " | " + pair.LabelForRole(moment.Role) + "：" + preview);
            }

            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", system.ToString()),
                new DeepSeekMessageData("user", "请输出 JSON。")
            };
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<RealmBatchOutputData>(
                llm, messages, x => x != null && x.items != null && x.items.Count > 0,
                "分类输出缺少 items。", CancellationToken.None);

            var byEventId = new Dictionary<string, MomentRecord>(StringComparer.Ordinal);
            foreach (var moment in batch)
            {
                byEventId[moment.SourceEventId] = moment;
                var match = System.Text.RegularExpressions.Regex.Match(
                    moment.SourceEventId ?? string.Empty, @"(\d+):(\d+)$");
                if (match.Success)
                {
                    byEventId[match.Groups[1].Value] = moment;
                    byEventId[match.Groups[2].Value] = moment;
                }
            }
            var updated = 0;
            foreach (var item in output.items ?? new List<RealmBatchItemData>())
            {
                MomentRecord moment;
                if (item == null || !byEventId.TryGetValue((item.event_id ?? string.Empty).Trim(), out moment)) continue;
                var realm = NormalizeRealm(item.realm);
                if (realm == TraceRealmValues.Unclassified) continue;
                context.Migration.UpdateMomentRealm(moment.Id, realm, DeriveEvidence(moment, realm));
                updated += 1;
            }
            Console.WriteLine("分类批次：处理 " + batch.Count + " 条，更新 " + updated + " 条。");
            return updated;
        }

        private static string StartLine(string sourceEventId)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                sourceEventId ?? string.Empty, @"(\d+):(\d+)$");
            return match.Success ? match.Groups[1].Value : sourceEventId ?? string.Empty;
        }

        private static string NormalizeRealm(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == TraceRealmValues.ExternalWorld || value == "external") return TraceRealmValues.ExternalWorld;
            if (value == TraceRealmValues.SharedScene || value == "shared") return TraceRealmValues.SharedScene;
            if (value == TraceRealmValues.Meta) return TraceRealmValues.Meta;
            if (value == TraceRealmValues.ExplicitFiction || value == "fiction") return TraceRealmValues.ExplicitFiction;
            return TraceRealmValues.Unclassified;
        }

        private static string DeriveEvidence(MomentRecord moment, string realm)
        {
            if (moment.Role == "ass") return EvidenceTypeValues.AssPerformed;
            if (realm == TraceRealmValues.SharedScene) return EvidenceTypeValues.SharedSceneDeclared;
            if (realm == TraceRealmValues.ExplicitFiction) return EvidenceTypeValues.ExplicitFiction;
            if (realm == TraceRealmValues.Meta) return EvidenceTypeValues.PluginObserved;
            return EvidenceTypeValues.UserReported;
        }

        private static IEnumerable<DateTime> EnumerateDays(MigrationContext context, DateRange range)
        {
            var result = new List<DateTime>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var moment in context.Migration.GetImportedMomentsInRange(0, long.MaxValue))
            {
                var dayKey = DateRange.DayKey(moment.CreatedUnixMs);
                if (!seen.Add(dayKey)) continue;
                var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                if (range.Contains(day)) result.Add(day);
            }
            return result;
        }
    }
}
