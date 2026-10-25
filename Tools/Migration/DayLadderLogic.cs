using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 时间阶梯榜单（日 → 周 → 月 → 年 → 永久）：
    /// 日榜 10 条，周榜、月榜、年榜、永久榜各 3 条；事件与认知各自持榜。晋升 = 移动——事件升入上层后从下层移出，跨层不重复。
    /// 上层候选 = 本层已有条目 ∪ 下层胜者，滚动重建不会丢失已晋升条目。
    /// 全部幂等：重跑同周期整批替换。
    /// </summary>
    public static class DayLadderLogic
    {
        private static async Task<HashSet<string>> SelectCandidatesAsync(MigrationContext context, ILlmClient llm,
            List<ReviewSelectionLogic.Candidate> candidates, string purpose, int capacity)
            => (await ReviewSelectionLogic.SelectAsync(llm, candidates, purpose, capacity, 6000, fitBudget: true, store: context.Store))
                .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        public static async Task<List<LadderItemRecord>> RankDayAsync(
            MigrationContext context,
            PairIdentity pair,
            string dayKey,
            List<EventIndexRecord> dayIndexes,
            ILlmClient llm)
        {
            var promoted = context.Migration.GetPromotedRefIds();
            var ordered = (dayIndexes ?? new List<EventIndexRecord>())
                .Where(x => x != null && !promoted.Contains(x.Id))
                .OrderBy(x => x.TimeUnixMs)
                .ToList();
            if (ordered.Count == 0)
            {
                Console.WriteLine("  事件日榜：当天没有可上榜的事件索引（或全部已晋升），跳过。");
                return new List<LadderItemRecord>();
            }

            var selected = await SelectCandidatesAsync(context, llm, ordered.Select(x => new ReviewSelectionLogic.Candidate {
                Id = x.Id, Description = x.TimeLabel + "｜" + x.EventSummary, EvidenceChars = x.EventSummary?.Length ?? 0 }).ToList(),
                dayKey + "事件日榜；只选重要事件，原因只解释意义，不扩写经历", LadderContextLogic.Capacity("day"));
            ordered.RemoveAll(x => !selected.Contains(x.Id));

            var prompt = BuildRankPrompt(pair, dayKey, ordered, "日榜·事件", LadderContextLogic.Capacity("day"));
            var output = await AskRankAsync(context, llm, dayKey + "|event", prompt, ordered.Count, LadderContextLogic.Capacity("day"));
            var items = ResolveDayItems(output, ordered, dayKey);
            Console.WriteLine("  事件日榜 " + items.Count + " 条：" +
                              string.Join("；", items.Select(x => x.Rank + "." + x.Label).Take(LadderContextLogic.Capacity("day"))));
            return items;
        }

        /// <summary>认知日榜：当天新形成的认知（与事件并列、各持榜单），候选为当天创建的 active 认知。</summary>
        public static async Task<List<LadderItemRecord>> RankDayCognitionsAsync(
            MigrationContext context,
            PairIdentity pair,
            string dayKey,
            List<CognitionSliceRecord> dayCognitions,
            ILlmClient llm)
        {
            var promoted = context.Migration.GetPromotedRefIds();
            var ordered = (dayCognitions ?? new List<CognitionSliceRecord>())
                .Where(x => x != null && !promoted.Contains(x.Id))
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
            if (ordered.Count == 0)
            {
                Console.WriteLine("  认知日榜：当天没有新认知，跳过。");
                return new List<LadderItemRecord>();
            }

            var selected = await SelectCandidatesAsync(context, llm, ordered.Select(x => new ReviewSelectionLogic.Candidate {
                Id = x.Id, Description = x.Summary, EvidenceChars = x.Summary?.Length ?? 0 }).ToList(),
                dayKey + "认知日榜；同义认识只需一项，条件与反证不能混掉", LadderContextLogic.Capacity("day"));
            ordered.RemoveAll(x => !selected.Contains(x.Id));

            var prompt = BuildCognitionRankPrompt(pair, dayKey, ordered);
            var output = await AskRankAsync(context, llm, dayKey + "|cognition", prompt, ordered.Count, LadderContextLogic.Capacity("day"));
            var items = ResolveDayCognitionItems(output, ordered, dayKey);
            Console.WriteLine("  认知日榜 " + items.Count + " 条：" +
                              string.Join("；", items.Select(x => x.Rank + "." + x.Label).Take(LadderContextLogic.Capacity("day"))));
            return items;
        }

        /// <summary>
        /// 当天日榜完成后，只滚动到期的上层榜单（普通日不发生任何晋升）：
        /// 周一 → 用上周 day 榜挑战周榜；1 号 → 用上月 week 榜挑战月榜；
        /// 年初 → 用去年 month 榜挑战年榜，再由年榜决定挑战永久榜。
        /// 晋升逐层发生：事件必须先活过 day → week → month → year，只有年榜能把条目送进永久榜。
        /// </summary>
        public static async Task PromoteAsync(
            MigrationContext context, PairIdentity pair, string dayKey, ILlmClient llm)
        {
            var day = ParseDay(dayKey);
            var calls = 0;
            if (day.DayOfWeek == DayOfWeek.Monday)
                calls += await PromoteWeekAsync(context, pair, llm, MondayOf(day).AddDays(-7), dayKey);
            if (day.Day == 1)
                calls += await PromoteMonthAsync(context, pair, llm, day.AddDays(-1).ToString("yyyy-MM"), dayKey);
            if (day.Month == 1 && day.Day == 1)
            {
                calls += await PromoteYearAsync(context, pair, llm, day.AddYears(-1).ToString("yyyy"), dayKey);
                calls += await PromoteForeverAsync(context, pair, llm, dayKey);
            }
            if (calls == 0)
            {
                var isBoundary = day.DayOfWeek == DayOfWeek.Monday || day.Day == 1;
                Console.WriteLine(isBoundary
                    ? "  今天是滚动边界，但下层无候选，上层榜单保持。"
                    : "  今天不是榜单滚动边界（周一/1 号/年初），日榜保持，不晋升。");
            }
            else
            {
                var pruned = context.Migration.PruneCrossTierLadderDuplicates();
                if (pruned > 0)
                    Console.WriteLine("  榜单跨层归一化：移除 " + pruned + " 条低层重复记录。");
            }
        }

        /// <summary>兜底：把日榜数据里出现的所有周/月/年周期全部重建一遍（含永久）。</summary>
        public static async Task<int> PromoteAllAsync(MigrationContext context, PairIdentity pair, ILlmClient llm)
        {
            var dayKeys = context.Migration.GetLadderPeriodKeys("day");
            var weeks = dayKeys.Select(x => MondayOf(ParseDay(x))).Distinct().OrderBy(x => x).ToList();
            var months = dayKeys.Select(x => x.Substring(0, 7)).Distinct().OrderBy(x => x).ToList();
            var years = dayKeys.Select(x => x.Substring(0, 4)).Distinct().OrderBy(x => x).ToList();
            var calls = 0;
            foreach (var week in weeks)
                calls += await PromoteWeekAsync(context, pair, llm, week);
            foreach (var month in months)
                calls += await PromoteMonthAsync(context, pair, llm, month);
            foreach (var year in years)
                calls += await PromoteYearAsync(context, pair, llm, year);
            calls += await PromoteForeverAsync(context, pair, llm);
            var pruned = context.Migration.PruneCrossTierLadderDuplicates();
            Console.WriteLine("  榜单晋升完成：共 " + calls + " 次调用（周/月/年/永久各 3 条，跨层不重复）。");
            if (pruned > 0)
                Console.WriteLine("  榜单跨层归一化：移除 " + pruned + " 条低层重复记录。");
            return calls;
        }

        private static async Task<int> PromoteWeekAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, DateTime monday, string operation = null)
        {
            var periodKey = monday.ToString("yyyy-MM-dd");
            var dayKeys = DaysOfWeek(monday);
            var lower = context.Migration.GetLadderItems("day", dayKeys);
            return await PromoteTierAsync(
                context, pair, llm, "week", periodKey, "周榜",
                lower, "day", dayKeys, operation);
        }

        private static async Task<int> PromoteMonthAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string monthKey, string operation = null)
        {
            var weekKeys = context.Migration.GetLadderPeriodKeys("week")
                .Where(x => x.StartsWith(monthKey, StringComparison.Ordinal)).ToList();
            var lower = context.Migration.GetLadderItems("week", weekKeys);
            return await PromoteTierAsync(
                context, pair, llm, "month", monthKey, "月榜",
                lower, "week", weekKeys, operation);
        }

        private static async Task<int> PromoteYearAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string yearKey, string operation = null)
        {
            var monthKeys = context.Migration.GetLadderPeriodKeys("month")
                .Where(x => x.StartsWith(yearKey, StringComparison.Ordinal)).ToList();
            var lower = context.Migration.GetLadderItems("month", monthKeys);
            return await PromoteTierAsync(
                context, pair, llm, "year", yearKey, "年榜",
                lower, "month", monthKeys, operation);
        }

        private static async Task<int> PromoteForeverAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string operation = null)
        {
            var yearKeys = context.Migration.GetLadderPeriodKeys("year");
            var lower = context.Migration.GetLadderItems("year", yearKeys);
            return await PromoteTierAsync(
                context, pair, llm, "forever", "forever", "永久榜",
                lower, "year", yearKeys, operation);
        }

        /// <summary>
        /// 晋升一层：候选 = 本层已有条目 ∪ 下层胜者（去重）；事件与认知不可比、各持榜单，
        /// 各自独立排名（周/月/年/永久各 ≤3 条，日榜 ≤10 条），合并后整批替换本层，
        /// 再把晋升的 RefId 从下层周期里移出（晋升=移动，跨层不重复）。
        /// </summary>
        private static async Task<int> PromoteTierAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string tier,
            string periodKey,
            string tierName,
            List<LadderItemRecord> lowerCandidates,
            string lowerTier,
            List<string> lowerScope, string operation = null)
        {
            var receiptKey = operation == null ? null : operation + ":" + tier + ":" + periodKey;
            if (receiptKey != null)
            {
                var prior = PuzzleViewLogic.Read<PromotionReceipt>(context.Store.LoadPluginDocument("ladder-receipt-v1", receiptKey));
                if (prior != null)
                {
                    if (prior.SourceStamp != PromotionSourceStamp(context, pair, prior.References))
                        throw new InvalidOperationException("已晋升榜单的来源已修订，拒绝复用旧提交收据。");
                    return 0;
                }
            }
            var existing = context.Migration.GetLadderItems(tier, new[] { periodKey });
            var all = existing
                .Concat(lowerCandidates ?? new List<LadderItemRecord>())
                .GroupBy(x => x.RefId)
                .Select(g => g.First())
                .ToList();
            if (all.Count == 0)
            {
                Console.WriteLine("  " + tierName + "（" + periodKey + "）：无候选，跳过。");
                return 0;
            }

            var sourceRefs = all.Select(x => x.RefId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sourceStamp = PromotionSourceStamp(context, pair, sourceRefs);
            var inputHash = IdentityReorganizationLogic.Digest(new { tier, periodKey, pair, all });
            var promotedRefs = new List<string>();
            var kept = new List<LadderItemRecord>();
            var kinds = all.Select(x => string.IsNullOrWhiteSpace(x.ListKind) ? "event" : x.ListKind)
                .Distinct().OrderBy(x => x).ToList();
            foreach (var kind in kinds)
            {
                var candidates = all
                    .Where(x => (string.IsNullOrWhiteSpace(x.ListKind) ? "event" : x.ListKind) == kind)
                    .OrderBy(x => x.Rank)
                    .ToList();
                if (candidates.Count == 0) continue;
                var kindName = kind == "cognition" ? "认知" : "事件";
                var selected = await SelectCandidatesAsync(context, llm, candidates.Select(x => new ReviewSelectionLogic.Candidate {
                    Id = x.RefId, Description = x.Label, EvidenceChars = x.Label?.Length ?? 0 }).ToList(),
                    periodKey + tierName + kindName + "；比较全部候选，只选本层重要认识或经历，不续写旧上榜原因", LadderContextLogic.Capacity(tier));
                candidates.RemoveAll(x => !selected.Contains(x.RefId));
                var prompt = BuildRankPrompt(pair, periodKey + "·" + kindName, candidates, tierName + kindName, LadderContextLogic.Capacity(tier));
                var output = await AskRankAsync(context, llm, periodKey + "|" + tier + "|" + kind, prompt, candidates.Count, LadderContextLogic.Capacity(tier));
                var aliasToCandidate = new Dictionary<string, LadderItemRecord>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < candidates.Count; i++) aliasToCandidate["i" + (i + 1)] = candidates[i];
                var rank = 1;
                foreach (var item in output.items ?? new List<LadderRankItemData>())
                {
                    if (item == null || rank > LadderContextLogic.Capacity(tier)) break;
                    LadderItemRecord source;
                    if (!aliasToCandidate.TryGetValue((item.index_alias ?? string.Empty).Trim(), out source)) continue;
                    kept.Add(new LadderItemRecord
                    {
                        Id = periodKey + "|" + tier + "|" + kind + "|" + rank,
                        Tier = tier,
                        PeriodKey = periodKey,
                        ListKind = kind,
                        Rank = rank,
                        RefId = source.RefId,
                        RefKind = source.RefKind,
                        Label = source.Label,
                        Reason = (item.reason ?? "").Trim(),
                        CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    promotedRefs.Add(source.RefId);
                    rank += 1;
                }
            }
            context.Migration.CommitCoreProgress(() => {
                if (PromotionSourceStamp(context, pair, sourceRefs) != sourceStamp)
                    throw new InvalidOperationException("晋升期间来源已修订，未提交旧结果。");
                context.Migration.ReplaceLadder(tier, periodKey, kept);
                context.Migration.RemoveFromLadder(lowerTier, lowerScope, promotedRefs);
                if (receiptKey != null) context.Migration.SaveProgressDocument("ladder-receipt-v1", receiptKey,
                    TraceJson.ToJson(new PromotionReceipt { References = sourceRefs, SourceStamp = sourceStamp, InputHash = inputHash }));
            });
            Console.WriteLine("  " + tierName + "（" + periodKey + "）已写入 " + kept.Count + " 条（事件/认知各持榜单，下层已移出）。");
            return 1;
        }

        public sealed class PromotionReceipt
        {
            public List<string> References;
            public string SourceStamp;
            public string InputHash;
        }
        private static string PromotionSourceStamp(MigrationContext context, PairIdentity pair, List<string> ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            return IdentityReorganizationLogic.Digest(new { pair,
                Events = context.Migration.GetActiveEventIndexes().Where(x => wanted.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal),
                Cognitions = context.Store.GetCognitionNodes(5000).Where(x => wanted.Contains(x.Id)).OrderBy(x => x.Id, StringComparer.Ordinal) });
        }

        private static async Task<LadderRankOutputData> AskRankAsync(
            MigrationContext context, ILlmClient llm, string dayKey, string prompt, int count, int capacity)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", prompt),
                new DeepSeekMessageData("user", CorePrompts.Migration.RankUser)
            };
            var output = await ReviewWorkLogic.CompleteAsync<LadderRankOutputData>(context.Store, "rank:" + dayKey, llm, messages,
                x => RankError(x, count, capacity, false),
                x => x.items.Select((item, i) => new ReviewWorkLogic.TextPart("items[" + i + "].reason", 120, () => item.reason, v => item.reason = v)));
            DayBuilder.LogCall(context, dayKey, "ladder_rank", 0,
                "榜单：" + (output.items ?? new List<LadderRankItemData>()).Count + " 条",
                TraceJson.ToJson(output));
            return output;
        }

        private static string RankError(LadderRankOutputData output, int count, int capacity, bool checkLength = true)
        {
            if (output?.items == null || output.items.Count < 1 || output.items.Count > capacity)
                return "items须有1～" + capacity + "项，不凑满榜单。";
            var ids = Enumerable.Range(1, count).Select(i => "i" + i).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (output.items.Any(x => x == null || !ids.Contains((x.index_alias ?? "").Trim())) ||
                output.items.Select(x => x.index_alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.items.Count)
                return "index_alias须为本次候选编号且每项只出现一次。";
            if (checkLength && output.items.Any(x => (x.reason?.Trim().Length ?? 0) > 120)) return "reason超过120字，只写上榜意义，不复述经历。";
            return null;
        }

        private static List<LadderItemRecord> ResolveDayItems(
            LadderRankOutputData output,
            List<EventIndexRecord> ordered,
            string dayKey)
        {
            var aliasToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ordered.Count; i++)
            {
                aliasToId["i" + (i + 1)] = ordered[i].Id;
                aliasToId[ordered[i].Id] = ordered[i].Id;
            }
            var items = new List<LadderItemRecord>();
            var rank = 1;
            foreach (var item in output.items ?? new List<LadderRankItemData>())
            {
                if (item == null || rank > LadderContextLogic.Capacity("day")) break;
                string refId;
                if (!aliasToId.TryGetValue((item.index_alias ?? string.Empty).Trim(), out refId)) continue;
                var index = ordered.FirstOrDefault(x => x.Id == refId);
                if (index == null) continue;
                items.Add(new LadderItemRecord
                {
                    Id = dayKey + "|day|" + rank,
                    Tier = "day",
                    PeriodKey = dayKey,
                    ListKind = "event",
                    Rank = rank,
                    RefId = refId,
                    RefKind = "event_index",
                    Label = index.EventSummary,
                    Reason = (item.reason ?? "").Trim(),
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                rank += 1;
            }
            return items;
        }

        private static List<LadderItemRecord> ResolveDayCognitionItems(
            LadderRankOutputData output,
            List<CognitionSliceRecord> ordered,
            string dayKey)
        {
            var aliasToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ordered.Count; i++)
            {
                aliasToId["i" + (i + 1)] = ordered[i].Id;
                aliasToId[ordered[i].Id] = ordered[i].Id;
            }
            var items = new List<LadderItemRecord>();
            var rank = 1;
            foreach (var item in output.items ?? new List<LadderRankItemData>())
            {
                if (item == null || rank > LadderContextLogic.Capacity("day")) break;
                string refId;
                if (!aliasToId.TryGetValue((item.index_alias ?? string.Empty).Trim(), out refId)) continue;
                var cognition = ordered.FirstOrDefault(x => x.Id == refId);
                if (cognition == null) continue;
                items.Add(new LadderItemRecord
                {
                    Id = dayKey + "|day|cognition|" + rank,
                    Tier = "day",
                    PeriodKey = dayKey,
                    ListKind = "cognition",
                    Rank = rank,
                    RefId = refId,
                    RefKind = "cognition",
                    Label = cognition.Summary,
                    Reason = (item.reason ?? "").Trim(),
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                rank += 1;
            }
            return items;
        }

        private static string BuildCognitionRankPrompt(
            PairIdentity pair, string periodKey, List<CognitionSliceRecord> ordered)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.CognitionRankIntro(periodKey)));
            for (var i = 0; i < ordered.Count; i++)
                builder.AppendLine("i" + (i + 1) + " | " + ordered[i].Summary + " | 置信 " + ordered[i].Confidence.ToString("0.00"));
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.CognitionRankAsk(LadderContextLogic.Capacity("day")));
            builder.AppendLine(CorePrompts.Migration.RankJsonSchema);
            return builder.ToString();
        }

        private static string BuildRankPrompt(
            PairIdentity pair, string periodKey, List<EventIndexRecord> ordered, string tierName, int topSize)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.EventRankIntro(tierName, periodKey)));
            for (var i = 0; i < ordered.Count; i++)
                builder.AppendLine("i" + (i + 1) + " | " + ordered[i].TimeLabel + "·" +
                    (string.IsNullOrWhiteSpace(ordered[i].MoodLabel) ? "心情未知" : ordered[i].MoodLabel) +
                    " | " + ordered[i].EventSummary);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventRankAsk(topSize));
            builder.AppendLine(CorePrompts.Migration.RankJsonSchema);
            return builder.ToString();
        }

        private static string BuildRankPrompt(
            PairIdentity pair, string periodKey, List<LadderItemRecord> candidates, string tierName, int topSize)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.EventRankIntro(tierName, periodKey)));
            for (var i = 0; i < candidates.Count; i++)
                builder.AppendLine("i" + (i + 1) + " | " + candidates[i].Label);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventRankAsk(topSize));
            builder.AppendLine(CorePrompts.Migration.RankJsonSchema);
            return builder.ToString();
        }

        private static DateTime ParseDay(string dayKey)
        {
            return DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static DateTime MondayOf(DateTime day)
        {
            var offset = ((int)day.DayOfWeek + 6) % 7;
            return day.Date.AddDays(-offset);
        }

        private static List<string> DaysOfWeek(DateTime monday)
        {
            var days = new List<string>();
            for (var i = 0; i < 7; i++)
                days.Add(monday.AddDays(i).ToString("yyyy-MM-dd"));
            return days;
        }

    }

    [Serializable]
    public sealed class LadderRankOutputData
    {
        public List<LadderRankItemData> items = new List<LadderRankItemData>();
    }

    [Serializable]
    public sealed class LadderRankItemData
    {
        public int rank;
        public string index_alias;
        public string reason;
    }
}
