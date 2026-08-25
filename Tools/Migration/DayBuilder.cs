using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Tools.Memory;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 新路线单天构筑：
    /// 1 天对话（已入库 moments）→ 批量构筑第四层多维索引 + 条目（一句话总结客观）
    /// → 细节浸染（助手第一人称逐条写细节）→ 日终复盘三张小卡（我是谁/对方是谁/我们的关系 必须成长）。
    /// 时间维度由 TimeLanguage 确定性翻译。榜单暂停，不建 ladder。
    /// </summary>
    public static class DayBuilder
    {
        public static async Task<int> RunAsync(MigrationContext context, string[] args)
        {
            var dayKey = CliArgs.Value(args, "--day");
            if (string.IsNullOrWhiteSpace(dayKey))
                throw new InvalidOperationException("需要 --day yyyy-MM-dd。");
            if (context.Migration.IsDayCompleted(dayKey))
            {
                // 完成标记早于临时样本清理：即使上次进程恰好在两者之间退出，也能在这里补清理。
                context.Store.RetireDayRuntimeSamples(MigrationContext.ConversationId, dayKey);
                Console.WriteLine("日终复盘已完成，跳过重复构筑：" + dayKey);
                return 0;
            }
            context.Migration.SaveReviewState(new ReviewStateRecord
            {
                DayKey = dayKey,
                Status = "running",
                Error = string.Empty
            });
            try
            {
                return await RunCoreAsync(context, args, dayKey);
            }
            catch (Exception exception)
            {
                // 完成标记是提交点。提交后的样本清理/打印即使异常，也不能把已成功复盘改回 failed；
                // 下次启动会走完成分支重试清理，而不会重复长期沉淀。
                if (!context.Migration.IsDayCompleted(dayKey))
                    context.Migration.SaveReviewState(new ReviewStateRecord
                {
                    DayKey = dayKey,
                    Status = "failed",
                    Error = Limit(exception.Message, 1000)
                });
                throw;
            }
        }

        private static async Task<int> RunCoreAsync(MigrationContext context, string[] args, string dayKey)
        {
            var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
            var moments = context.Migration.GetUnbuiltMomentsInRange(
                range.DayStartMs(day), range.DayEndMs(day));
            var pair = context.RequirePair();
            var llm = context.RequireLlm();
            if (moments.Count == 0)
            {
                MigrationLive.Start(context);
                return await EmptyDayCycleAsync(context, pair, llm, dayKey);
            }
            MigrationLive.Start(context);
            var router = new HierarchicalVectorRouterLogic(new BagOfCharsVectorEncoder());
            RebuildRouter(context, pair, router);

            var referenceTime = new DateTimeOffset(day.Date.AddHours(12), MigrationContext.ChinaOffset);
            var lastMomentId = moments[moments.Count - 1].Id;
            var cardsBefore = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("构筑前四张卡", cardsBefore, pair);

            var chunks = ChunkMoments(moments);
            var dayIndexes = new List<EventIndexRecord>();
            var dayEntries = new List<EventEntryRecord>();
            var evidenceByEntry = new Dictionary<string, List<MomentRecord>>(StringComparer.Ordinal);
            var observationCalls = 0;

            foreach (var chunk in chunks)
            {
                var chunkText = string.Join("\n", chunk.Select(x => FormatMoment(x, pair)));
                var route = router.Route(chunkText);
                var conceptIds = route.Concepts.Select(x => x.Node.Id).ToList();
                var activeTags = context.Store.GetActiveLifeTags();
                var recentTags = activeTags.OrderByDescending(x => x.ActivationCount).Take(30).ToList();
                var indexCandidates = context.Migration.GetEventIndexCandidates(
                    activeTags.Select(x => x.Id).ToList(), 20);
                var anchor = DateTimeOffset.FromUnixTimeMilliseconds(chunk[0].CreatedUnixMs)
                    .ToOffset(MigrationContext.ChinaOffset);
                var periodLabel = TimeLanguage.PeriodZh(TimeLanguage.PeriodOf(anchor));
                var dayKindLabel = TimeLanguage.DayKindLabel(anchor);

                var prompt = ReplayPrompts.BuildEventObservationPrompt(
                    pair, chunk, route, indexCandidates, recentTags, periodLabel, dayKindLabel);
                var messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", prompt),
                    new DeepSeekMessageData("user", CorePrompts.Migration.ObserveUser)
                };
                var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DayEventOutputData>(
                    llm, messages,
                    x => x != null && !string.IsNullOrWhiteSpace(x.perception_summary),
                    "事件观察输出缺少 perception_summary。", CancellationToken.None);
                observationCalls += 1;
                Console.WriteLine("  块" + observationCalls + " 观察：" + Limit(output.perception_summary, 80));
                LogCall(context, dayKey, "event_observation", observationCalls,
                    Limit(output.perception_summary, 80), TraceJson.ToJson(output));

                // 复用旧路的 Tag 创建/激活通道：只带 Tag 信息，不带事实。
                var tagOutput = new MemoryObservationOutputData
                {
                    perception_summary = output.perception_summary ?? string.Empty,
                    fact_decision = output.event_decision ?? string.Empty,
                    selected_tag_ids = output.selected_tag_ids ?? new List<string>(),
                    new_tags = output.new_tags ?? new List<NewLifeTagWriteData>(),
                    fact_writes = new List<SensoryFactWriteData>(),
                    fact_wakes = new List<SensoryFactWakeData>()
                };
                var tagAliases = BuildTagAliases(activeTags);
                RepairSelectedTagIds(tagOutput, tagAliases);
                context.Migration.CreateAndActivateTags(
                    tagOutput, chunk[chunk.Count - 1], conceptIds, pair);

                var tagByLabel = context.Store.GetActiveLifeTags()
                    .GroupBy(x => x.Label)
                    .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
                var aliasToIndex = new Dictionary<string, EventIndexRecord>(StringComparer.Ordinal);
                foreach (var candidate in indexCandidates) aliasToIndex[candidate.Id] = candidate;

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var write in output.event_writes ?? new List<ReplayPrompts.EventWriteItemData>())
                {
                    if (write == null || string.IsNullOrWhiteSpace(write.event_summary)) continue;
                    var tagIds = ResolveTagIds(write.tag_ids, write.new_tag_names, tagAliases, tagByLabel);
                    if (tagIds.Count == 0)
                    {
                        Console.WriteLine("  [警告] 事件「" + Limit(write.event_summary, 20) + "」无有效 Tag，丢弃。");
                        continue;
                    }
                    var index = new EventIndexRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        TagIds = string.Join(",", tagIds),
                        TimeLabel = periodLabel,
                        DayKindLabel = dayKindLabel,
                        TimeUnixMs = anchor.ToUnixTimeMilliseconds(),
                        PlaceLabel = Limit(write.place, 20),
                        PersonLabel = Limit(write.person, 20),
                        EventSummary = Limit(write.event_summary, 80),
                        MoodLabel = Limit(write.mood, 12),
                        FirstMomentId = chunk[0].Id,
                        Status = "active",
                        CreatedUnixMs = now,
                        UpdatedUnixMs = now
                    };
                    context.Migration.SaveEventIndex(index);
                    dayIndexes.Add(index);
                    var entry = new EventEntryRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        IndexId = index.Id,
                        Summary = Limit(write.entry_summary, 80),
                        Detail = string.Empty,
                        SourceMomentId = chunk[chunk.Count - 1].Id,
                        Realm = NormalizeRealm(write.realm),
                        CreatedUnixMs = now
                    };
                    context.Migration.AppendEventEntry(entry);
                    dayEntries.Add(entry);
                    evidenceByEntry[entry.Id] = chunk;
                }

                foreach (var append in output.event_appends ?? new List<ReplayPrompts.EventAppendItemData>())
                {
                    if (append == null || string.IsNullOrWhiteSpace(append.entry_summary)) continue;
                    var target = ResolveIndexAlias(append.index_alias, aliasToIndex);
                    if (target == null)
                    {
                        Console.WriteLine("  [警告] 追加目标索引无法解析：" + Limit(append.index_alias, 20) + "。");
                        continue;
                    }
                    var entry = new EventEntryRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        IndexId = target.Id,
                        Summary = Limit(append.entry_summary, 80),
                        Detail = string.Empty,
                        SourceMomentId = chunk[chunk.Count - 1].Id,
                        Realm = NormalizeRealm(null),
                        CreatedUnixMs = now
                    };
                    context.Migration.AppendEventEntry(entry);
                    dayEntries.Add(entry);
                    evidenceByEntry[entry.Id] = chunk;
                    if (!dayIndexes.Any(x => x.Id == target.Id)) dayIndexes.Add(target);
                }
            }

            // ---------- 细节浸染：助手第一人称（4 路并行） ----------
            // 占位/空卡回退到种子文件（档案卡以用户填写的种子为准）。
            var personalityCard = IdentityCardLogic.ResolveBody(
                IdentityCardSlotValues.Personality,
                Card(cardsBefore, IdentityCardSlotValues.Personality).Body, pair);
            var userProfileCard = IdentityCardLogic.ResolveBody(
                IdentityCardSlotValues.UserProfile,
                Card(cardsBefore, IdentityCardSlotValues.UserProfile).Body, pair);
            var detailCalls = 0;
            var detailGate = new SemaphoreSlim(4);
            var detailTasks = dayEntries.Select(async entry =>
            {
                List<MomentRecord> evidence;
                if (!evidenceByEntry.TryGetValue(entry.Id, out evidence)) return;
                await detailGate.WaitAsync();
                try
                {
                    var index = dayIndexes.FirstOrDefault(x => x.Id == entry.IndexId);
                    var indexLine = index == null ? string.Empty
                        : index.TimeLabel + "（" + index.DayKindLabel + "）·" + index.PlaceLabel + "·"
                          + index.PersonLabel + "·" + index.EventSummary + "·" + index.MoodLabel;
                    var prompt = ReplayPrompts.BuildDetailPrompt(
                        pair, personalityCard, userProfileCard, evidence, indexLine, entry.Summary, 0, 200);
                    var messages = new List<DeepSeekMessageData>
                    {
                        new DeepSeekMessageData("system", prompt),
                        new DeepSeekMessageData("user", CorePrompts.Migration.DetailUser)
                    };
                    var detailOutput = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DetailOutputData>(
                        llm, messages, x => x != null && !string.IsNullOrWhiteSpace(x.detail),
                        "细节输出缺少 detail。", CancellationToken.None);
                    entry.Detail = SmartTrim(detailOutput.detail ?? string.Empty, 200);
                    context.Migration.UpdateEventEntryDetail(entry.Id, entry.Detail);
                    var n = Interlocked.Increment(ref detailCalls);
                    Console.WriteLine("  细节「" + Limit(entry.Summary, 20) + "」：" + Limit(entry.Detail, 60));
                    LogCall(context, dayKey, "detail", n,
                        "细节：" + Limit(entry.Summary, 40), TraceJson.ToJson(detailOutput));
                }
                finally
                {
                    detailGate.Release();
                }
            });
            await Task.WhenAll(detailTasks);

            // ---------- 条目语义向量：一句话总结编码（幂等，召回拼装用） ----------
            try
            {
                var encoder = context.RequireEncoder();
                var embedded = EntryEmbedder.EmbedAll(dayEntries, context.Vectors, encoder);
                if (embedded > 0) Console.WriteLine("  条目语义向量已编码 " + embedded + " 条。");
            }
            catch (Exception exception)
            {
                Console.WriteLine("  [警告] 条目向量编码失败（Host 召回将退回字符路由）：" + exception.Message);
            }

            // ---------- 认知形成：跑完事件构筑后，直接用当天新增事件提炼第一人称理解 ----------
            await RunCognitionFormationAsync(
                context, pair, llm, dayKey, dayIndexes, dayEntries, lastMomentId);

            // ---------- 日终三卡复盘 + 内心全字段同步 ----------
            var cardsNow = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            var reviewOutput = await RunDayReviewAsync(
                context, pair, llm, dayKey, cardsNow, userProfileCard, dayIndexes, dayEntries);
            var changed = ApplyReviewOutput(context, pair, reviewOutput, cardsNow, lastMomentId, false);
            Console.WriteLine();
            Console.WriteLine("三卡与内心变化：" + (changed.Count == 0 ? "（无变化）" : string.Join("；", changed)));

            // ---------- 当天排序：日榜（事件/认知各持榜单）+ 周/月/年/永久晋升 ----------
            var dayCognitions = context.Migration.GetCognitionsCreatedInRange(
                range.DayStartMs(day), range.DayEndMs(day));
            await RankDayLadderAsync(context, pair, llm, dayKey, dayIndexes, dayCognitions);
            await DayLadderLogic.PromoteAsync(context, pair, dayKey, llm);

            // ---------- 标记当天 Moment 已归档（实时归档已标记的不会重复） ----------
            var markedBuilt = context.Migration.MarkMomentsBuiltByRange(
                range.DayStartMs(day), range.DayEndMs(day));
            if (markedBuilt > 0) Console.WriteLine("  已标记 " + markedBuilt + " 条 Moment 为已归档（built）。");
            context.Migration.MarkDayCompleted(dayKey);
            context.Store.RetireDayRuntimeSamples(MigrationContext.ConversationId, dayKey);

            var cardsAfter = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("构筑后四张卡", cardsAfter, pair);
            PrintDayResult(dayKey, moments, dayIndexes, dayEntries, observationCalls, detailCalls, context);
            return 0;
        }

        /// <summary>空天仍是真实的一天：短卡保持原样，内心写下这一天的时间感；手上没结束的事不清掉。</summary>
        private static async Task<int> EmptyDayCycleAsync(
            MigrationContext context, PairIdentity pair, ILlmClient llm, string dayKey)
        {
            Console.WriteLine("空天循环：" + dayKey + " 没有 Moment——仍复盘这一天的时间感。");
            var cardsNow = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            var profile = Card(cardsNow, IdentityCardSlotValues.UserProfile);
            var userProfileCard = IdentityCardLogic.ResolveBody(
                IdentityCardSlotValues.UserProfile, profile.Body, pair);
            var reviewOutput = await RunDayReviewAsync(
                context, pair, llm, dayKey, cardsNow, userProfileCard,
                new List<EventIndexRecord>(), new List<EventEntryRecord>());
            var changed = ApplyReviewOutput(context, pair, reviewOutput, cardsNow, string.Empty, true);
            Console.WriteLine("三卡与内心变化：" + (changed.Count == 0 ? "（无变化）" : string.Join("；", changed)));
            var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
            var dayCognitions = context.Migration.GetCognitionsCreatedInRange(
                range.DayStartMs(day), range.DayEndMs(day));
            await RankDayLadderAsync(
                context, pair, llm, dayKey, new List<EventIndexRecord>(), dayCognitions);
            await DayLadderLogic.PromoteAsync(context, pair, dayKey, llm);
            context.Migration.MarkDayCompleted(dayKey);
            context.Store.RetireDayRuntimeSamples(MigrationContext.ConversationId, dayKey);
            PrintCards("空天复盘后四张卡", context.Store.LoadIdentityCards(MigrationContext.ConversationId), pair);
            return 0;
        }

        private static async Task<ReplayPrompts.DayCardReviewOutputData> RunDayReviewAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string dayKey,
            List<IdentityCardRecord> cardsNow,
            string userProfileCard,
            List<EventIndexRecord> dayIndexes,
            List<EventEntryRecord> dayEntries)
        {
            var selfCard = Card(cardsNow, IdentityCardSlotValues.Self).Body;
            var otherCard = Card(cardsNow, IdentityCardSlotValues.Other).Body;
            var relationCard = Card(cardsNow, IdentityCardSlotValues.Relation).Body;
            var expressionCard = Card(cardsNow, IdentityCardSlotValues.ExpressionHabit).Body;
            var trajectory = context.Store.LoadDayTrajectory(dayKey);
            var todayNewItems = context.Store.GetTodayNewItemsByDay(
                MigrationContext.ConversationId, dayKey);
            var currentInner = context.Store.LoadOrCreateInnerRuntime(MigrationContext.ConversationId);
            var currentLife = context.LifeState == null
                ? null : context.LifeState.Load(MigrationContext.ConversationId);
            var reviewPrompt = ReplayPrompts.BuildDayCardReviewPrompt(
                pair, dayKey, selfCard, otherCard, relationCard, expressionCard,
                userProfileCard, dayIndexes, dayEntries,
                trajectory == null ? string.Empty : trajectory.Text,
                todayNewItems,
                InnerLifeLogic.FormatForMind(currentInner),
                FormatLifeState(currentLife));
            var reviewMessages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", reviewPrompt),
                new DeepSeekMessageData("user", CorePrompts.Migration.DayCardUser)
            };
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.DayCardReviewOutputData>(
                llm, reviewMessages,
                x => x != null && x.cards != null && x.cards.Count > 0,
                "三卡复盘输出缺少 cards。", CancellationToken.None);
            LogCall(context, dayKey, "card_review", 0,
                "三卡复盘：" + Limit(output.summary, 60), TraceJson.ToJson(output));
            return output;
        }

        private static string FormatLifeState(LifeStateData life)
        {
            if (life == null) return string.Empty;
            var location = string.IsNullOrWhiteSpace(life.location) ? "未知" : BodySceneValues.Label(life.location);
            var activity = string.IsNullOrWhiteSpace(life.activity) ? "空闲" : life.activity;
            return "位置=" + location + "；活动=" + activity +
                   (string.IsNullOrWhiteSpace(life.activity_detail) ? string.Empty : "｜" + life.activity_detail);
        }

        /// <summary>应用复盘输出：三卡只写真正变化的；内心全字段经 Reduce 同步（空字段=保留现状）。空天不清手上未结束的事。</summary>
        private static List<string> ApplyReviewOutput(
            MigrationContext context,
            PairIdentity pair,
            ReplayPrompts.DayCardReviewOutputData reviewOutput,
            List<IdentityCardRecord> cardsNow,
            string lastMomentId,
            bool emptyDay)
        {
            var changed = new List<string>();
            foreach (var card in reviewOutput.cards ?? new List<ReplayPrompts.CardUpdateData>())
            {
                if (card == null || !IdentityCardSlotValues.IsKnown(card.slot)
                    || card.slot == IdentityCardSlotValues.Personality) continue;
                var body = pair.RewriteRecordedText((card.body ?? string.Empty).Trim());
                if (body.Length == 0) continue;
                if (card.slot == IdentityCardSlotValues.UserProfile &&
                    body.IndexOf("姓名", StringComparison.Ordinal) < 0) continue;
                if (body.Length > IdentityCardSlotValues.BodyLimit(card.slot))
                    body = SmartTrim(body, IdentityCardSlotValues.BodyLimit(card.slot));
                if (body == Card(cardsNow, card.slot).Body) continue;
                context.Store.SaveIdentityCard(MigrationContext.ConversationId, card.slot, body, lastMomentId);
                changed.Add(IdentityCardSlotValues.Title(card.slot, pair) + "：" + Limit(card.reason, 40));
            }

            // 内心：attention 是会代谢的碎片；ongoing 是共享场景。旧 unfinished 字段不再恢复。
            var attention = (reviewOutput.inner_attention == null || reviewOutput.inner_attention.Count == 0)
                ? null
                : reviewOutput.inner_attention;
            var ongoing = (reviewOutput.inner_ongoing_activity ?? string.Empty).Trim();
            var proposed = new InnerRuntimeWriteData
            {
                narrative = pair.RewriteRecordedText((reviewOutput.inner_narrative ?? string.Empty).Trim()),
                mood = (reviewOutput.inner_mood ?? string.Empty).Trim(),
                relationship_update = (reviewOutput.inner_relationship_lens ?? string.Empty).Trim(),
                ongoing_activity = emptyDay && ongoing.Length == 0 ? null : ongoing,
                unfinished_intent = string.Empty,
                attention = attention
            };
            var hasInner = proposed.narrative.Length > 0 || proposed.mood.Length > 0 ||
                           proposed.relationship_update.Length > 0 || proposed.ongoing_activity.Length > 0 ||
                           (attention != null && attention.Count > 0);
            if (hasInner)
            {
                var sourceMomentId = string.IsNullOrWhiteSpace(lastMomentId)
                    ? context.Store.GetRecentMoments(MigrationContext.ConversationId, 1)
                        .Select(x => x.Id).FirstOrDefault()
                    : lastMomentId;
                if (string.IsNullOrWhiteSpace(sourceMomentId))
                    sourceMomentId = "day-review";
                var currentInner = context.Store.LoadOrCreateInnerRuntime(MigrationContext.ConversationId);
                var nextInner = InnerLifeLogic.Reduce(
                    currentInner, proposed, sourceMomentId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                context.Store.SaveInnerRuntime(nextInner);
                changed.Add("内心：" + Limit(nextInner.Narrative, 60));
            }
            return changed;
        }

        /// <summary>认知形成：用当天新增事件提炼第一人称理解，写认知切片（最多 3 条，挂生命标签）。</summary>
        private static async Task<List<CognitionSliceRecord>> RunCognitionFormationAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string dayKey,
            List<EventIndexRecord> dayIndexes,
            List<EventEntryRecord> dayEntries,
            string lastMomentId)
        {
            var activeCognitions = context.Migration.GetAllActiveCognitions();
            var activeTags = context.Store.GetActiveLifeTags();
            var cardsNow = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            var userPronoun = IdentityCardLogic.UserPronoun(cardsNow, pair);
            var prompt = ReplayPrompts.BuildCognitionFormationPrompt(
                pair, dayKey, activeCognitions, activeTags, dayIndexes, dayEntries, userPronoun);
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", prompt),
                new DeepSeekMessageData("user", CorePrompts.Migration.CognitionUser)
            };
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReplayPrompts.CognitionFormationOutputData>(
                llm, messages, x => x != null, "认知复盘输出无效。", CancellationToken.None);
            var mutations = (output.cognitions ?? new List<BrainCognitionWriteData>())
                .Where(x => x != null).ToList();
            // 幂等守卫：与已有 active 认知同文的 create 直接跳过（回填重复跑不产生重复认知）。
            var existingSummaries = new HashSet<string>(
                (activeCognitions ?? new List<CognitionSliceRecord>()).Select(x => x.Summary ?? string.Empty),
                StringComparer.Ordinal);
            var filtered = mutations.Where(x =>
                !(string.Equals(x.operation, "create", StringComparison.OrdinalIgnoreCase) &&
                  existingSummaries.Contains((x.summary ?? string.Empty).Trim()))).ToList();
            // 本管线没有事实切片：证据一律用 Moment，清掉模型可能填的 evidence_fact_ids。
            foreach (var m in filtered) m.evidence_fact_ids = new List<string>();
            var changed = context.Store.CommitCognitions(lastMomentId, filtered);
            LogCall(context, dayKey, "cognition_formation", 1,
                "认知：" + changed.Count + " 条", TraceJson.ToJson(output));
            Console.WriteLine("  认知形成：" + (changed.Count == 0
                ? "无变化"
                : changed.Count + " 条：" + string.Join("；", changed.Select(x => Limit(x.Summary, 30)))));
            return changed;
        }

        /// <summary>
        /// 认知回填：主线等已有事件、尚无认知的库，逐天用当天已有事件跑认知形成，
        /// 再把新认知排进该天日榜（与事件并列）；跨层晋升由 promote-all 兜底。
        /// 幂等守卫：同文 create 会跳过，但 revise/reinforce 轻微累积，不建议频繁重跑。
        /// </summary>
        public static async Task<int> BackfillCognitionsAsync(MigrationContext context, string[] args)
        {
            var pair = context.RequirePair();
            var llm = context.RequireLlm();
            var fromDay = CliArgs.Value(args, "--from");
            var dayKeys = context.Migration.GetDistinctEventDays()
                .Where(x => string.IsNullOrWhiteSpace(fromDay) ||
                            string.Compare(x, fromDay, StringComparison.Ordinal) >= 0)
                .ToList();
            Console.WriteLine("认知回填：共 " + dayKeys.Count + " 个有事件的天" +
                              (string.IsNullOrWhiteSpace(fromDay) ? "" : "（自 " + fromDay + " 起续跑）") +
                              "，逐天用当天事件跑认知形成。");
            var total = 0;
            foreach (var dayKey in dayKeys)
            {
                var day = DateTime.ParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var range = DateRange.Parse(new[] { "--from", dayKey, "--to", dayKey });
                var start = range.DayStartMs(day);
                var end = range.DayEndMs(day);
                var dayIndexes = context.Migration.GetActiveEventIndexes()
                    .Where(x => x.TimeUnixMs >= start && x.TimeUnixMs < end)
                    .OrderBy(x => x.TimeUnixMs)
                    .ToList();
                if (dayIndexes.Count == 0) continue;
                var dayEntries = context.Migration.GetEntriesByIndexIds(dayIndexes.Select(x => x.Id).ToList());
                var lastMomentId = dayIndexes[dayIndexes.Count - 1].FirstMomentId ?? "day-review";
                var changed = await RunCognitionFormationAsync(
                    context, pair, llm, dayKey, dayIndexes, dayEntries, lastMomentId);
                if (changed.Count > 0)
                {
                    total += changed.Count;
                    var cognitionItems = await DayLadderLogic.RankDayCognitionsAsync(
                        context, pair, dayKey, changed, llm);
                    var existing = context.Migration.GetLadder("day", dayKey);
                    var combined = existing.Concat(cognitionItems).ToList();
                    if (combined.Count > 0)
                        context.Migration.ReplaceLadder("day", dayKey, combined);
                }
                Console.WriteLine("  " + dayKey + "：+" + changed.Count + " 条认知（累计 " + total + "）");
            }
            Console.WriteLine("认知回填完成：共 " + total + " 条认知。");
            return 0;
        }

        /// <summary>
        /// 认知合并去重：先合并归一化后完全同文的重复，再合并二元组 Jaccard≥0.75 的近义认知；
        /// 保留置信度最高者（并列取最早），合并后清理阶梯里的重复指针。被合并项只是停用，不物理删除。
        /// </summary>
        public static int DedupeCognitions(MigrationContext context)
        {
            var cognitions = context.Migration.GetAllActiveCognitions();
            var merges = new List<Tuple<string, string>>();
            var merged = new HashSet<string>(StringComparer.Ordinal);

            var groups = cognitions
                .Where(x => x != null && NormalizeCognition(x.Summary).Length >= 2)
                .GroupBy(x => NormalizeCognition(x.Summary), StringComparer.Ordinal)
                .Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                var list = group.ToList();
                var keeper = KeepCognition(list);
                foreach (var dup in list.Where(x => x.Id != keeper.Id))
                {
                    merges.Add(Tuple.Create(dup.Id, keeper.Id));
                    merged.Add(dup.Id);
                }
            }

            var remaining = cognitions.Where(x => x != null && !merged.Contains(x.Id)).ToList();
            for (var i = 0; i < remaining.Count; i++)
            {
                if (merged.Contains(remaining[i].Id)) continue;
                for (var j = i + 1; j < remaining.Count; j++)
                {
                    if (merged.Contains(remaining[j].Id)) continue;
                    if (BigramJaccard(remaining[i].Summary, remaining[j].Summary) < 0.75f) continue;
                    var keeper = KeepCognition(new List<CognitionSliceRecord> { remaining[i], remaining[j] });
                    var dup = keeper.Id == remaining[i].Id ? remaining[j] : remaining[i];
                    merges.Add(Tuple.Create(dup.Id, keeper.Id));
                    merged.Add(dup.Id);
                }
            }

            foreach (var merge in merges)
            {
                var keep = cognitions.FirstOrDefault(x => x.Id == merge.Item2);
                context.Migration.MergeCognitionInto(merge.Item1, merge.Item2,
                    keep == null ? string.Empty : keep.Summary);
            }
            if (merges.Count > 0)
                context.Migration.RemoveDuplicateLadderRefs();
            Console.WriteLine("认知合并去重：合并 " + merges.Count + " 条，active 认知剩余 " +
                              context.Migration.GetAllActiveCognitions().Count + " 条。");
            return merges.Count;
        }

        private static CognitionSliceRecord KeepCognition(List<CognitionSliceRecord> list)
        {
            return list.OrderByDescending(x => x.Confidence).ThenBy(x => x.CreatedUnixMs).First();
        }

        private static string NormalizeCognition(string text)
        {
            var builder = new StringBuilder();
            foreach (var ch in text ?? string.Empty)
                if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            return builder.ToString();
        }

        private static float BigramJaccard(string a, string b)
        {
            var sa = NormalizeCognition(a);
            var sb = NormalizeCognition(b);
            if (sa.Length < 3 || sb.Length < 3) return 0f;
            var setA = Bigrams(sa);
            var setB = Bigrams(sb);
            var inter = setA.Intersect(setB).Count();
            var union = setA.Union(setB).Count();
            return union == 0 ? 0f : (float)inter / union;
        }

        private static HashSet<string> Bigrams(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < text.Length; i++) set.Add(text.Substring(i, 2));
            return set;
        }

        /// <summary>日榜排序：事件与认知各自排名、合并后整批替换当天 day 榜。</summary>
        private static async Task RankDayLadderAsync(
            MigrationContext context,
            PairIdentity pair,
            ILlmClient llm,
            string dayKey,
            List<EventIndexRecord> dayIndexes,
            List<CognitionSliceRecord> dayCognitions)
        {
            var eventItems = await DayLadderLogic.RankDayAsync(context, pair, dayKey, dayIndexes, llm);
            var cognitionItems = await DayLadderLogic.RankDayCognitionsAsync(
                context, pair, dayKey, dayCognitions, llm);
            var combined = new List<LadderItemRecord>();
            combined.AddRange(eventItems);
            combined.AddRange(cognitionItems);
            if (combined.Count > 0)
                context.Migration.ReplaceLadder("day", dayKey, combined);
            else
                Console.WriteLine("  日榜：当天没有可上榜的事件或认知。");
        }

        /// <summary>打印四张卡（含 revision）。</summary>
        public static int PrintCardsCommand(MigrationContext context)
        {
            var pair = context.Store.LoadPairIdentity();
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("四张身份小卡", cards, pair);
            return 0;
        }

        private static void PrintCards(string title, List<IdentityCardRecord> cards, PairIdentity pair)
        {
            Console.WriteLine("========== " + title + " ==========");
            foreach (var card in cards)
            {
                Console.WriteLine("【" + IdentityCardSlotValues.Title(card.Slot, pair) + "】rev" + card.Revision);
                Console.WriteLine(card.Body);
                Console.WriteLine();
            }
        }

        private static void PrintDayResult(
            string dayKey,
            List<MomentRecord> moments,
            List<EventIndexRecord> indexes,
            List<EventEntryRecord> entries,
            int observationCalls,
            int detailCalls,
            MigrationContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("========== 构筑结果 " + dayKey + " ==========");
            builder.AppendLine("moments：" + moments.Count + "；观察调用：" + observationCalls
                + "；细节调用：" + detailCalls + "；索引：" + indexes.Count
                + "；条目：" + entries.Count);
            builder.AppendLine();
            foreach (var index in indexes)
            {
                var dateText = index.TimeUnixMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(index.TimeUnixMs)
                        .ToOffset(MigrationContext.ChinaOffset).ToString("yyyy年M月d日")
                    : string.Empty;
                builder.AppendLine("◆ " + dateText + index.TimeLabel + "（" + index.DayKindLabel + "）"
                    + (string.IsNullOrWhiteSpace(index.PlaceLabel) ? "" : "·" + index.PlaceLabel)
                    + (string.IsNullOrWhiteSpace(index.PersonLabel) ? "" : "·" + index.PersonLabel)
                    + "·" + index.EventSummary
                    + (string.IsNullOrWhiteSpace(index.MoodLabel) ? "" : "·" + index.MoodLabel));
                foreach (var entry in entries.Where(x => x.IndexId == index.Id))
                {
                    builder.AppendLine("   └ " + entry.Summary + " [" + entry.Realm + "]");
                    if (!string.IsNullOrWhiteSpace(entry.Detail))
                        builder.AppendLine("       " + entry.Detail);
                }
            }
            var text = builder.ToString();
            var path = Path.Combine(context.DataDirectory, "day_build_report.txt");
            File.WriteAllText(path, text, Encoding.UTF8);
            Console.WriteLine(text);
            Console.WriteLine("构筑报告已写入：" + path);
        }

        private static List<string> ResolveTagIds(
            List<string> tagIds,
            List<string> newTagNames,
            Dictionary<string, string> aliases,
            Dictionary<string, string> tagByLabel)
        {
            var result = new List<string>();
            foreach (var id in tagIds ?? new List<string>())
            {
                string full;
                if (aliases.TryGetValue(id ?? string.Empty, out full) && !result.Contains(full)) result.Add(full);
            }
            foreach (var name in newTagNames ?? new List<string>())
            {
                string id;
                if (tagByLabel.TryGetValue((name ?? string.Empty).Trim(), out id) && !result.Contains(id)) result.Add(id);
            }
            return result.Take(8).ToList();
        }

        /// <summary>Tag 完整 ID → 完整 ID 与去掉前缀的后缀 ID 的别名表，修复 LLM 截断 ID 的问题。</summary>
        private static Dictionary<string, string> BuildTagAliases(List<LifeTagRecord> tags)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags ?? new List<LifeTagRecord>())
            {
                aliases[tag.Id] = tag.Id;
                var dot = tag.Id.LastIndexOf('.');
                if (dot >= 0 && dot < tag.Id.Length - 1)
                {
                    var suffix = tag.Id.Substring(dot + 1);
                    if (!aliases.ContainsKey(suffix)) aliases[suffix] = tag.Id;
                }
            }
            return aliases;
        }

        private static void RepairSelectedTagIds(
            MemoryObservationOutputData output, Dictionary<string, string> aliases)
        {
            if (output == null) return;
            var selected = new List<string>();
            foreach (var id in output.selected_tag_ids ?? new List<string>())
            {
                string full;
                var mapped = aliases.TryGetValue(id ?? string.Empty, out full) ? full : id;
                if (mapped != null && !selected.Contains(mapped)) selected.Add(mapped);
            }
            output.selected_tag_ids = selected;
        }

        private static EventIndexRecord ResolveIndexAlias(
            string alias, Dictionary<string, EventIndexRecord> byId)
        {
            alias = (alias ?? string.Empty).Trim();
            if (alias.Length == 0) return null;
            EventIndexRecord exact;
            if (byId.TryGetValue(alias, out exact)) return exact;
            if (alias.Length >= 12)
            {
                var matches = byId.Keys.Where(x => x.EndsWith(alias, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) return byId[matches[0]];
            }
            return null;
        }

        private static IdentityCardRecord Card(List<IdentityCardRecord> cards, string slot)
        {
            return cards.First(x => x.Slot == slot);
        }

        private static string NormalizeRealm(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "external_world" || value == "external") return TraceRealmValues.ExternalWorld;
            if (value == "shared_scene" || value == "shared") return TraceRealmValues.SharedScene;
            if (value == "meta") return TraceRealmValues.Meta;
            if (value == "explicit_fiction" || value == "fiction") return TraceRealmValues.ExplicitFiction;
            return TraceRealmValues.SharedScene;
        }

        private static List<List<MomentRecord>> ChunkMoments(List<MomentRecord> moments)
        {
            var result = new List<List<MomentRecord>>();
            var current = new List<MomentRecord>();
            var chars = 0;
            foreach (var moment in moments)
            {
                var length = (moment.Content ?? string.Empty).Length;
                if (current.Count >= 18 || (current.Count > 0 && chars + length > 6000))
                {
                    result.Add(current);
                    current = new List<MomentRecord>();
                    chars = 0;
                }
                current.Add(moment);
                chars += length;
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        private static void RebuildRouter(
            MigrationContext context, PairIdentity pair, HierarchicalVectorRouterLogic router)
        {
            var ontology = LifeTagVectorLogic.BuildOntology(
                context.Store, CoreVectorOntologyFactory.Create(pair));
            router.Build(ontology);
        }

        private static string FormatMoment(MomentRecord moment, PairIdentity pair)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(moment.CreatedUnixMs)
                .ToOffset(MigrationContext.ChinaOffset).ToString("HH:mm");
            return time + " " + pair.LabelForRole(moment.Role) + "：" + (moment.Content ?? string.Empty);
        }

        /// <summary>把一次 LLM 调用写入留痕表并广播到实时监视控制台。</summary>
        internal static void LogCall(
            MigrationContext context, string dayKey, string kind, int chunkIndex, string digest, string outputJson)
        {
            context.Migration.SaveCallLog(new ReplayCallLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DayKey = dayKey,
                CallKind = kind,
                ChunkIndex = chunkIndex,
                OutputJson = Limit(outputJson, 60000)
            });
            MigrationLive.Instance?.Notify(kind, digest, Limit(outputJson, 4000));
        }

        /// <summary>在 max 字内按句界收尾：找最后一个句号/问号/叹号/省略号收口，绝不切半句。</summary>
        private static string SmartTrim(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length < max) return value;
            var window = value.Substring(0, Math.Min(value.Length, max));
            var lastEnd = -1;
            foreach (var marker in new[] { '。', '！', '？', '…' })
            {
                var index = window.LastIndexOf(marker);
                if (index > lastEnd) lastEnd = index;
            }
            if (lastEnd >= 80) return value.Substring(0, lastEnd + 1).Trim();
            return window.Trim();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
