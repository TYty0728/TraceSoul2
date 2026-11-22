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

        private const string PeriodCursor = "closed-ladder-period";
        private const string ReadingStyle = "先找出每条值得记住的核心，再重新成句。日常通常一句，围绕最有辨识度的细节；有变化的事保留转折或结果。认知第一句就说领会到什么，必要条件可以带上。保留当事人的口吻，分清感受、转述和事实。材料只供阅读。";

        public static async Task<int> CompactReadingAsync(MigrationContext context, DateTimeOffset now)
        {
            using var lease = ReviewExecutionLease.Acquire(context.Migration.DataDirectory);
            var sources = LadderContextLogic.ReadingBoards(context.Store.GetAllLadderItems(), now)
                .SelectMany(board => board.Items)
                .Where(item => string.IsNullOrWhiteSpace(item.ReadingLabel)).ToList();
            if (sources.Count == 0) return 0;

            var input = sources.Select((item, i) => new { index = i + 1, kind = item.ListKind, text = item.Label });
            var messages = new List<DeepSeekMessageData> {
                new("system", "【精简榜单】让每条一眼能读懂并想起来。" + ReadingStyle +
                    "例如，一整段找餐馆、看菜单和商量时间的经过，可以收成‘她看中了巷子里的面馆，我们约好下雨那天去。’一整段争论与和解得出的认知可以写成‘她更愿意先把分歧说清楚，等我们都想好了再做决定。’" +
                    "已经好读的原文略过。只返回改写的项，每个编号各写各的。" +
                    "返回JSON {\"items\":[{\"index\":1,\"summary\":\"精简后的原意\"}]}，都合适时返回{\"items\":[]}。"),
                new("user", TraceJson.ToJson(input))
            };
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<LadderReadingOutput>(context.RequireLlm(),
                messages, null, "请返回榜单短文。", CancellationToken.None,
                validationError: x => ReadingError(x, sources.Count));
            var summaries = new Dictionary<int, string>();
            foreach (var item in output.items)
            {
                var reading = ShorterReading(item.summary, sources[item.index - 1].Label);
                if (reading != null) summaries.Add(item.index, reading);
            }
            context.Migration.SaveLadderReadings(sources, summaries);
            return summaries.Count;
        }

        private static string ReadingError(LadderReadingOutput output, int count)
        {
            if (output.items == null) return "items须为数组，可为空。";
            var seen = new HashSet<int>();
            foreach (var item in output.items)
            {
                if (item == null || item.index < 1 || item.index > count || !seen.Add(item.index))
                    return "index须引用本次实际条目，且每项只出现一次。";
            }
            return null;
        }

        private static string ShorterReading(string candidate, string original)
        {
            var reading = candidate?.Trim();
            return !string.IsNullOrEmpty(reading) && reading.Length < (original?.Trim().Length ?? 0) ? reading : null;
        }

        // through is the last reviewed memory day, inclusive. Weeks belong to their Monday's month.
        public static async Task PromoteDueAsync(MigrationContext context, DateTime through)
        {
            using var lease = ReviewExecutionLease.Acquire(context.Migration.DataDirectory);
            var end = MemoryDayLogic.StartOf(through.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToUnixTimeMilliseconds();
            if (context.Store.GetUnbuiltMemoryDayKeysBefore(end).Count > 0)
                throw new InvalidOperationException("周期内仍有未完成日复盘的来源，请先完成日复盘。");
            var days = context.Migration.GetDoneDayKeys().Concat(context.Migration.GetLadderPeriodKeys("day"));
            var weeks = days.Select(x => MondayOf(ParseDay(x))).Distinct().OrderBy(x => x).ToList();
            string Latest(string tier)
            {
                var json = context.Store.LoadPluginDocument(PeriodCursor, tier);
                var saved = string.IsNullOrEmpty(json) ? "" : TraceJson.FromJson<string>(json);
                var existing = context.Migration.GetLadderPeriodKeys(tier).LastOrDefault();
                return string.CompareOrdinal(saved, existing) > 0 ? saved : existing;
            }
            var weekCursor = Latest("week");
            foreach (var week in weeks.Where(w => w.AddDays(6) <= through))
            {
                var key = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(key, weekCursor) <= 0) continue;
                await PromoteWeekAsync(context, context.RequirePair(), context.RequireLlm(), week, PeriodCursor);
            }

            var monthCursor = Latest("month");
            var months = context.Migration.GetLadderPeriodKeys("week").Select(x => ParseDay(x.Substring(0, 7) + "-01"))
                .Distinct().OrderBy(x => x);
            foreach (var month in months)
            {
                var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var lastSunday = MondayOf(month.AddMonths(1).AddDays(-1)).AddDays(6);
                if (lastSunday > through || string.CompareOrdinal(key, monthCursor) <= 0) continue;
                await PromoteMonthAsync(context, context.RequirePair(), context.RequireLlm(), key, PeriodCursor);
            }
        }

        private static async Task<int> PromoteWeekAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, DateTime monday, string operation = null)
        {
            var periodKey = monday.ToString("yyyy-MM-dd");
            var dayKeys = DaysOfWeek(monday);
            var lower = context.Migration.GetLadderItems("day", dayKeys);
            // Modern daily reviews store changed cognition IDs in their checkpoint, outside the event board.
            var changed = dayKeys.Select(day => context.Store.LoadPluginDocument(DayBuilder.DailyReviewDocuments, day))
                .Where(json => !string.IsNullOrWhiteSpace(json)).Select(json => TraceJson.FromJson<DayBuilder.DailyReviewState>(json))
                .Where(state => state.Done).SelectMany(state => state.ChangedCognitions).ToHashSet(StringComparer.Ordinal);
            if (changed.Count > 0)
            {
                var promoted = context.Migration.GetPromotedRefIds();
                foreach (var node in context.Store.GetCognitionReviewCandidates(MigrationContext.ConversationId, false, true)
                    .Where(n => changed.Contains(n.Id) && !promoted.Contains(n.Id)))
                    lower.Add(new LadderItemRecord { ListKind = "cognition", RefKind = "cognition", RefId = node.Id, Label = node.Summary });
            }
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
                if (operation == PeriodCursor)
                    context.Migration.SaveProgressDocument(PeriodCursor, tier, TraceJson.ToJson(periodKey));
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
                        ReadingLabel = ShorterReading(item.reading, LadderContextLogic.ReadingLabel(source)) ?? source.ReadingLabel,
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
                if (operation == PeriodCursor)
                {
                    context.Migration.PruneCrossTierLadderDuplicates();
                    context.Migration.SaveProgressDocument(PeriodCursor, tier, TraceJson.ToJson(periodKey));
                }
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

        private static string BuildRankPrompt(
            PairIdentity pair, string periodKey, List<LadderItemRecord> candidates, string tierName, int topSize)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine(pair.Apply(CorePrompts.Migration.EventRankIntro(tierName, periodKey)));
            for (var i = 0; i < candidates.Count; i++)
                builder.AppendLine("i" + (i + 1) + " | " + candidates[i].Label);
            builder.AppendLine();
            builder.AppendLine(CorePrompts.Migration.EventRankAsk(topSize));
            builder.AppendLine("入选项的reading是榜单简读。" + ReadingStyle +
                "每项基于自己的原文写更精炼的短文，原文已合适时reading填空。");
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
        public string reading;
    }

    [Serializable]
    public sealed class LadderReadingOutput
    {
        public List<LadderReadingItem> items;
    }

    [Serializable]
    public sealed class LadderReadingItem
    {
        public int index;
        public string summary;
    }
}
